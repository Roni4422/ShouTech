using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
using ShouTech.Infra;

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
            // Recycle bin (migration 122): a binned entry is invisible
            // to the voucher windows — it can only come back through
            // the bin's restore.
            var notDeleted = _provider == "PostgreSQL" ? "is_deleted=false" : "is_deleted=0";
            header.CommandText = $"""
                SELECT journal_entry_id, company_id, entry_date, description, status,
                       reference_number, currency_code, exchange_rate, document_number
                FROM journal_entries
                WHERE company_id = @company AND document_number = @documentNumber AND {notDeleted}
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
        var hasLineBase = PartyLedger.ColumnExists(connection, null, _provider, "journal_entry_lines", "base_debit");
        await using (var lines = connection.CreateCommand())
        {
            var baseAmountColumns = hasLineBase
                ? ", l.base_debit, l.base_credit, l.exchange_rate"
                : string.Empty;
            lines.CommandText = $"""
                SELECT l.line_number, l.account_id, a.account_code, a.account_name,
                       l.debit, l.credit, l.cost_center, l.note{baseAmountColumns}
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
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    // Step 7: base amounts as stored (migration 062
                    // backfill guarantees them on migrated databases);
                    // a NULL on an unmigrated fixture falls back to the
                    // header rate so the snapshot is never base-blind.
                    hasLineBase
                        ? reader.IsDBNull(8) ? FxRates.ToBase(Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture), exchangeRate) : Convert.ToDecimal(reader.GetValue(8), CultureInfo.InvariantCulture)
                        : FxRates.ToBase(Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture), exchangeRate),
                    hasLineBase
                        ? reader.IsDBNull(9) ? FxRates.ToBase(Convert.ToDecimal(reader.GetValue(5), CultureInfo.InvariantCulture), exchangeRate) : Convert.ToDecimal(reader.GetValue(9), CultureInfo.InvariantCulture)
                        : FxRates.ToBase(Convert.ToDecimal(reader.GetValue(5), CultureInfo.InvariantCulture), exchangeRate),
                    hasLineBase && !reader.IsDBNull(10) ? Convert.ToDecimal(reader.GetValue(10), CultureInfo.InvariantCulture) : exchangeRate));
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

    public async Task SaveDraftAsync(Guid companyId, JournalEntry entry, string userId, CancellationToken cancellationToken = default, VoucherPartyLinkRequest? partyLink = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (partyLink is not null)
        {
            // G23: validate the link BEFORE opening the transaction so a
            // malformed link never starts a draft write at all; the row
            // itself is inserted inside the transaction below.
            if (string.IsNullOrWhiteSpace(partyLink.CompanyCode))
                throw new ArgumentException("The voucher party link requires a company code.", nameof(partyLink));
            if (partyLink.PartyKind is not ("C" or "S"))
                throw new ArgumentException("The voucher party link party kind must be 'C' or 'S'.", nameof(partyLink));
            if (partyLink.PartyId <= 0)
                throw new ArgumentException("The voucher party link requires a resolved party.", nameof(partyLink));
            if (partyLink.Amount <= 0)
                throw new ArgumentException("The voucher party link amount must be greater than zero.", nameof(partyLink));
            if (!((partyLink.DocKind == "RCV" && partyLink.PartyKind == "C")
                  || (partyLink.DocKind == "PAY" && partyLink.PartyKind == "S")))
                throw new ArgumentException(
                    "Cash receipt vouchers must be linked to customers and payment vouchers must be linked to suppliers.", nameof(partyLink));
        }
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

            // Step 7 (J-5): resolve the effective rate ONCE (the entry's
            // own rate is explicit here — JournalEntry requires > 0 —
            // base-currency entries resolve to 1) and write every line's
            // base-currency amounts alongside its transaction amounts.
            var fx = FxRates.Resolve(connection, transaction, _provider, companyId,
                entry.CurrencyCode, entry.EntryDate, entry.ExchangeRate);
            var hasLineBase = PartyLedger.ColumnExists(connection, transaction, _provider, "journal_entry_lines", "base_debit");
            var hasHeaderBase = PartyLedger.ColumnExists(connection, transaction, _provider, "journal_entries", "base_currency_code");

            await using (var command = CreateCommand(connection, transaction,
                hasHeaderBase
                    ? "INSERT INTO journal_entries (journal_entry_id, company_id, entry_date, description, status, created_by, fiscal_period_id, reference_number, currency_code, exchange_rate, document_number, base_currency_code) VALUES (@id, @company, @date, @description, @status, @actor, @period, @reference, @currency, @rate, @documentNumber, @baseCurrency)"
                    : "INSERT INTO journal_entries (journal_entry_id, company_id, entry_date, description, status, created_by, fiscal_period_id, reference_number, currency_code, exchange_rate, document_number) VALUES (@id, @company, @date, @description, @status, @actor, @period, @reference, @currency, @rate, @documentNumber)"))
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
                Add(command, "@rate", fx.Rate);
                Add(command, "@documentNumber", (object?)entry.DocumentNumber ?? DBNull.Value);
                if (hasHeaderBase)
                    Add(command, "@baseCurrency", (object?)fx.BaseCurrency ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var baseAmounts = FxRates.ConvertBalanced(
                entry.Lines.Select(l => (l.Debit, l.Credit)).ToList(), fx.Rate);
            var lineNumber = 0;
            foreach (var line in entry.Lines)
            {
                await using var command = CreateCommand(connection, transaction,
                    hasLineBase
                        ? "INSERT INTO journal_entry_lines (journal_entry_id, line_number, account_id, debit, credit, cost_center, note, base_debit, base_credit, exchange_rate) VALUES (@id, @line, @account, @debit, @credit, @costCenter, @note, @baseDebit, @baseCredit, @rate)"
                        : "INSERT INTO journal_entry_lines (journal_entry_id, line_number, account_id, debit, credit, cost_center, note) VALUES (@id, @line, @account, @debit, @credit, @costCenter, @note)");
                Add(command, "@id", FormatGuid(entry.Id));
                Add(command, "@line", ++lineNumber);
                Add(command, "@account", FormatGuid(line.AccountId));
                Add(command, "@debit", line.Debit);
                Add(command, "@credit", line.Credit);
                Add(command, "@costCenter", (object?)line.CostCenter ?? DBNull.Value);
                Add(command, "@note", (object?)line.Note ?? DBNull.Value);
                if (hasLineBase)
                {
                    Add(command, "@baseDebit", baseAmounts[lineNumber - 1].BaseDebit);
                    Add(command, "@baseCredit", baseAmounts[lineNumber - 1].BaseCredit);
                    Add(command, "@rate", fx.Rate);
                }
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await AddAuditAsync(connection, transaction, entry.Id, 1, userId, "Draft created", cancellationToken).ConfigureAwait(false);

            // G23: the voucher party link is written INSIDE this draft
            // transaction (previously PartyLedgerSvc wrote it on a
            // separate connection after the draft committed, so a crash
            // left a silently unlinked draft whose posting then settled
            // no party). A missing links table fails the whole draft —
            // fail closed, never a link-less voucher.
            if (partyLink is not null)
            {
                if (!PartyLedger.TableExists(connection, transaction, _provider, "voucher_party_links"))
                    throw new InvalidOperationException(
                        "Voucher party-link schema is missing. Apply database migration 061 before saving a party-linked voucher.");
                await using var link = CreateCommand(connection, transaction, """
                    INSERT INTO voucher_party_links
                        (journal_entry_id, company_id, party_kind, party_id, party_code, doc_kind, doc_no, amount, currency_code, created_by)
                    VALUES
                        (@id, @company, @kind, @party, @code, @docKind, @docNo, @amount, @currency, @user)
                    """);
                Add(link, "@id", FormatGuid(entry.Id));
                Add(link, "@company", partyLink.CompanyCode.Trim());
                Add(link, "@kind", partyLink.PartyKind);
                Add(link, "@party", partyLink.PartyId);
                Add(link, "@code", partyLink.PartyCode.Trim());
                Add(link, "@docKind", partyLink.DocKind);
                Add(link, "@docNo", partyLink.DocNo.Trim());
                Add(link, "@amount", partyLink.Amount);
                Add(link, "@currency", string.IsNullOrWhiteSpace(partyLink.CurrencyCode) ? "SAR" : partyLink.CurrencyCode.Trim());
                Add(link, "@user", userId.Trim());
                await link.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

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

            // Step 7 (J-5): same rate resolution + dual amounts as
            // SaveDraftAsync — an edited draft's lines are rewritten with
            // base amounts at the entry's (possibly changed) rate.
            var fx = FxRates.Resolve(connection, transaction, _provider, companyId,
                entry.CurrencyCode, entry.EntryDate, entry.ExchangeRate);
            var hasLineBase = PartyLedger.ColumnExists(connection, transaction, _provider, "journal_entry_lines", "base_debit");
            var hasHeaderBase = PartyLedger.ColumnExists(connection, transaction, _provider, "journal_entries", "base_currency_code");

            // The document number is the entry's identity (windows load by
            // it) and is deliberately NOT updated.
            await using (var update = CreateCommand(connection, transaction,
                hasHeaderBase
                    ? "UPDATE journal_entries SET entry_date = @date, description = @description, reference_number = @reference, currency_code = @currency, exchange_rate = @rate, fiscal_period_id = @period, base_currency_code = @baseCurrency WHERE journal_entry_id = @id AND company_id = @company AND status = @draft"
                    : "UPDATE journal_entries SET entry_date = @date, description = @description, reference_number = @reference, currency_code = @currency, exchange_rate = @rate, fiscal_period_id = @period WHERE journal_entry_id = @id AND company_id = @company AND status = @draft"))
            {
                Add(update, "@date", entryDateValue);
                Add(update, "@description", entry.Description);
                Add(update, "@reference", (object?)entry.ReferenceNumber ?? DBNull.Value);
                Add(update, "@currency", entry.CurrencyCode);
                Add(update, "@rate", fx.Rate);
                Add(update, "@period", _provider == "PostgreSQL" ? (object)Guid.Parse(periodId) : periodId);
                if (hasHeaderBase)
                    Add(update, "@baseCurrency", (object?)fx.BaseCurrency ?? DBNull.Value);
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

            var baseAmounts = FxRates.ConvertBalanced(
                entry.Lines.Select(l => (l.Debit, l.Credit)).ToList(), fx.Rate);
            var lineNumber = 0;
            foreach (var line in entry.Lines)
            {
                await using var command = CreateCommand(connection, transaction,
                    hasLineBase
                        ? "INSERT INTO journal_entry_lines (journal_entry_id, line_number, account_id, debit, credit, cost_center, note, base_debit, base_credit, exchange_rate) VALUES (@id, @line, @account, @debit, @credit, @costCenter, @note, @baseDebit, @baseCredit, @rate)"
                        : "INSERT INTO journal_entry_lines (journal_entry_id, line_number, account_id, debit, credit, cost_center, note) VALUES (@id, @line, @account, @debit, @credit, @costCenter, @note)");
                Add(command, "@id", FormatGuid(journalEntryId));
                Add(command, "@line", ++lineNumber);
                Add(command, "@account", FormatGuid(line.AccountId));
                Add(command, "@debit", line.Debit);
                Add(command, "@credit", line.Credit);
                Add(command, "@costCenter", (object?)line.CostCenter ?? DBNull.Value);
                Add(command, "@note", (object?)line.Note ?? DBNull.Value);
                if (hasLineBase)
                {
                    Add(command, "@baseDebit", baseAmounts[lineNumber - 1].BaseDebit);
                    Add(command, "@baseCredit", baseAmounts[lineNumber - 1].BaseCredit);
                    Add(command, "@rate", fx.Rate);
                }
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

    public async Task<IReadOnlyList<BudgetWarning>> PostAsync(Guid journalEntryId, string userId, CancellationToken cancellationToken = default)
    {
        if (journalEntryId == Guid.Empty) throw new ArgumentException("Journal entry is required.", nameof(journalEntryId));
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Phase B 055: BudgetGuard fills this sink with Stop violations
        // before throwing; the Blocked events are recorded after the
        // rollback below (an in-transaction row would roll back too).
        var budgetBlocked = new List<BudgetViolation>();
        IReadOnlyList<BudgetWarning> budgetWarnings = Array.Empty<BudgetWarning>();
        Guid companyId = Guid.Empty;
        try
        {
            DateOnly entryDate;
            string entryCurrency = "SAR";
            decimal entryRate = 1m;
            string storedPeriodId;
            await using (var header = CreateCommand(connection, transaction,
                "SELECT company_id, entry_date, currency_code, exchange_rate, fiscal_period_id FROM journal_entries WHERE journal_entry_id = @id"))
            {
                Add(header, "@id", FormatGuid(journalEntryId));
                await using var reader = await header.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Journal entry was not found.");
                companyId = Guid.Parse(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!);
                entryDate = _provider == "PostgreSQL"
                    ? reader.GetFieldValue<DateOnly>(1)
                    : DateOnly.Parse(Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
                if (!reader.IsDBNull(2)) entryCurrency = reader.GetString(2);
                if (!reader.IsDBNull(3)) entryRate = Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture);
                storedPeriodId = reader.IsDBNull(4) ? string.Empty : Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture)!;
            }

            // Revalidate the fiscal period at the moment of posting, not only
            // when the draft was saved. This uses the same resolver as
            // document-to-GL posting and rejects drafts whose period closed
            // or no longer matches the journal date.
            if (string.IsNullOrWhiteSpace(storedPeriodId))
                throw new InvalidOperationException("Journal entry has no assigned fiscal period.");
            var openPeriodId = GlPosting.ResolveOpenPeriod(
                connection, transaction, _provider, companyId, entryDate);
            if (Guid.Parse(openPeriodId) != Guid.Parse(storedPeriodId))
                throw new InvalidOperationException("The journal fiscal period is no longer open for its entry date.");

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

            // Step 7 (J-5): balance must ALSO hold in base currency.
            // Lines written by the current save paths always carry base
            // amounts; a legacy draft whose lines still lack them is
            // healed here from the header rate (the same conversion the
            // 062 backfill applies), inside the posting transaction.
            // A stored base imbalance (e.g. lines converted at mixed
            // rates that do not net) fails the posting with ACC-5001 —
            // mixed-rate entries must balance in base, by definition.
            if (PartyLedger.ColumnExists(connection, transaction, _provider, "journal_entry_lines", "base_debit"))
            {
                long missingBase;
                await using (var missing = CreateCommand(connection, transaction,
                    "SELECT COUNT(*) FROM journal_entry_lines WHERE journal_entry_id = @id AND base_debit IS NULL"))
                {
                    Add(missing, "@id", FormatGuid(journalEntryId));
                    missingBase = Convert.ToInt64(await missing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
                }
                if (missingBase > 0)
                {
                    var fx = FxRates.Resolve(connection, transaction, _provider, companyId, entryCurrency, entryDate, entryRate);
                    var stored = await LoadCostLinesAsync(connection, transaction, companyId, journalEntryId, cancellationToken).ConfigureAwait(false);
                    var healed = FxRates.ConvertBalanced(stored.Select(l => (l.Debit, l.Credit)).ToList(), fx.Rate);
                    for (var i = 0; i < stored.Count; i++)
                    {
                        await using var heal = CreateCommand(connection, transaction,
                            "UPDATE journal_entry_lines SET base_debit = @bd, base_credit = @bc, exchange_rate = @rate WHERE journal_entry_id = @id AND line_number = @line");
                        Add(heal, "@bd", healed[i].BaseDebit);
                        Add(heal, "@bc", healed[i].BaseCredit);
                        Add(heal, "@rate", fx.Rate);
                        Add(heal, "@id", FormatGuid(journalEntryId));
                        Add(heal, "@line", stored[i].LineNumber);
                        await heal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                await using var baseCheck = CreateCommand(connection, transaction,
                    "SELECT COALESCE(SUM(base_debit), 0), COALESCE(SUM(base_credit), 0) FROM journal_entry_lines WHERE journal_entry_id = @id");
                Add(baseCheck, "@id", FormatGuid(journalEntryId));
                await using var baseReader = await baseCheck.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await baseReader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("Unable to validate journal base amounts.");
                var baseDebit = Convert.ToDecimal(baseReader.GetValue(0), CultureInfo.InvariantCulture);
                var baseCredit = Convert.ToDecimal(baseReader.GetValue(1), CultureInfo.InvariantCulture);
                if (baseDebit <= 0 || baseDebit != baseCredit)
                    throw new AppError(ErrorCodes.JournalNotBalanced,
                        "Journal entry debit and credit totals must balance in base currency.");
            }

            // J-3 (Step 3): cost-center validation is shared with document
            // postings (GlPosting.ApplyCostCenters): unknown cost centers
            // fail with ACC-5009 and cost_required accounts without a
            // center fail with ACC-5010, inside the posting transaction.
            var postLines = await LoadCostLinesAsync(connection, transaction, companyId, journalEntryId, cancellationToken).ConfigureAwait(false);
            GlPosting.ApplyCostCenters(connection, transaction, _provider, companyId, journalEntryId, entryDate, postLines, enforceValidation: true);

            // Phase B 055: BudgetGuard, beside the cost splits it reads —
            // a Stop (ACC-5018/5019) throws here and the whole posting
            // rolls back, leaving the entry Draft; Warn warnings are
            // returned to the caller after the commit.
            budgetWarnings = BudgetGuard.Check(connection, transaction, _provider,
                companyId, journalEntryId, userId, budgetBlocked);

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

            // Step 4 settlement (sweep B-03): a cash voucher saved with a
            // resolved party carries a voucher_party_links row (migration
            // 061). Posting the voucher writes the party_ledger settlement
            // entry INSIDE this transaction — credit for a customer, debit
            // for a supplier — linked back to this journal entry so the
            // shared reversal core can mirror it symmetrically.
            if (PartyLedger.TableExists(connection, transaction, _provider, "voucher_party_links"))
            {
                string? linkCompany = null, linkKind = null, linkDocKind = null, linkDocNo = null, linkCurrency = null;
                long linkParty = 0;
                decimal linkAmount = 0m;
                // Supplier advances (migration 110): the link may flag the
                // voucher as an advance against one PO. Fixtures predating
                // 095 have no advance columns and simply never advance.
                var hasAdvanceCols = PartyLedger.ColumnExists(connection, transaction, _provider, "voucher_party_links", "po_doc_id");
                long? linkPoDocId = null;
                var linkIsAdvance = false;
                await using (var link = CreateCommand(connection, transaction,
                    "SELECT company_id, party_kind, party_id, doc_kind, doc_no, amount, currency_code"
                    + (hasAdvanceCols ? ", po_doc_id, is_advance" : "")
                    + " FROM voucher_party_links WHERE journal_entry_id = @id"))
                {
                    Add(link, "@id", FormatGuid(journalEntryId));
                    await using var reader = await link.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        linkCompany = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);
                        linkKind = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture);
                        linkParty = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
                        linkDocKind = Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture);
                        linkDocNo = Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture);
                        linkAmount = Convert.ToDecimal(reader.GetValue(5), CultureInfo.InvariantCulture);
                        linkCurrency = reader.IsDBNull(6) ? "SAR" : Convert.ToString(reader.GetValue(6), CultureInfo.InvariantCulture);
                        if (hasAdvanceCols)
                        {
                            linkPoDocId = reader.IsDBNull(7) ? null : Convert.ToInt64(reader.GetValue(7), CultureInfo.InvariantCulture);
                            linkIsAdvance = !reader.IsDBNull(8) && Convert.ToBoolean(reader.GetValue(8), CultureInfo.InvariantCulture);
                        }
                    }
                }

                if (linkCompany is not null && linkKind is not null && linkAmount > 0)
                {
                    await using (var companyCheck = CreateCommand(connection, transaction,
                        "SELECT company_id FROM companies WHERE company_code = @code"))
                    {
                        Add(companyCheck, "@code", linkCompany.Trim());
                        var linkedCompany = await companyCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                        if (linkedCompany is null or DBNull
                            || !Guid.TryParse(Convert.ToString(linkedCompany, CultureInfo.InvariantCulture), out var linkedCompanyId)
                            || linkedCompanyId != companyId)
                            throw new InvalidOperationException("The voucher party link belongs to a different company.");
                    }

                    if (!((linkDocKind == "RCV" && linkKind == "C")
                        || (linkDocKind == "PAY" && linkKind == "S")))
                        throw new InvalidOperationException(
                            "Cash receipt vouchers must be linked to customers and payment vouchers must be linked to suppliers.");

                    await using (var partyCheck = CreateCommand(connection, transaction,
                        "SELECT COUNT(*) FROM parties WHERE company_id = @company AND kind = @kind AND ledger_id = @party AND is_deleted = " + (_provider == "PostgreSQL" ? "FALSE" : "0")))
                    {
                        Add(partyCheck, "@company", linkCompany.Trim());
                        Add(partyCheck, "@kind", linkKind);
                        Add(partyCheck, "@party", linkParty);
                        if (Convert.ToInt64(await partyCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 1)
                            throw new InvalidOperationException("The linked customer or supplier no longer exists in this company.");
                    }

                    // Advance exception (Y1): a PAY voucher flagged as an
                    // advance must name a live PO of THIS company whose
                    // supplier is the linked party; its settlement debit
                    // then goes to Advances to Suppliers (1215), never to
                    // AP control (2201) — the money is a prepayment asset
                    // until PayRecWin allocation clears it against an
                    // invoice of that same PO.
                    if (linkIsAdvance)
                    {
                        if (linkDocKind != "PAY" || linkKind != "S" || linkPoDocId is null)
                            throw new AppError(ErrorCodes.AdvancePurchaseOrderMismatch,
                                "A supplier advance must be a payment voucher linked to a purchase order.", linkDocNo ?? string.Empty);
                        await using var po = CreateCommand(connection, transaction,
                            "SELECT party_id, status FROM purchase_documents WHERE doc_id = @po AND doc_kind = 'PO' AND "
                            + (_provider == "PostgreSQL" ? "is_deleted = FALSE" : "is_deleted = 0")
                            + " AND CAST(company_id AS TEXT) IN (@companyCode, @companyGuid)");
                        Add(po, "@po", linkPoDocId.Value);
                        Add(po, "@companyCode", linkCompany.Trim());
                        Add(po, "@companyGuid", FormatGuid(companyId));
                        await using var poReader = await po.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                        if (!await poReader.ReadAsync(cancellationToken).ConfigureAwait(false)
                            || poReader.IsDBNull(0)
                            || Convert.ToInt64(poReader.GetValue(0), CultureInfo.InvariantCulture) != linkParty
                            || !string.Equals(Convert.ToString(poReader.GetValue(1), CultureInfo.InvariantCulture), "Posted", StringComparison.OrdinalIgnoreCase))
                            throw new AppError(ErrorCodes.AdvancePurchaseOrderMismatch,
                                $"The advance purchase order '{linkPoDocId.Value}' is not a posted purchase order of the linked supplier in this company.", linkPoDocId.Value.ToString(CultureInfo.InvariantCulture));
                    }

                    var controlAccount = linkIsAdvance
                        ? GlAccounts.SupplierAdvances
                        : linkKind == "C" ? GlAccounts.Receivable : GlAccounts.Payable;
                    decimal totalDebit, totalCredit, controlDebit, controlCredit;
                    await using (var control = CreateCommand(connection, transaction, """
                        SELECT COALESCE(SUM(l.debit), 0), COALESCE(SUM(l.credit), 0),
                               COALESCE(SUM(CASE WHEN a.account_code = @account THEN l.debit ELSE 0 END), 0),
                               COALESCE(SUM(CASE WHEN a.account_code = @account THEN l.credit ELSE 0 END), 0)
                        FROM journal_entry_lines l
                        INNER JOIN chart_of_accounts a
                            ON a.account_id = l.account_id AND a.company_id = @company
                        WHERE l.journal_entry_id = @journal
                        """))
                    {
                        Add(control, "@company", FormatGuid(companyId));
                        Add(control, "@journal", FormatGuid(journalEntryId));
                        Add(control, "@account", controlAccount);
                        await using var controlReader = await control.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                        if (!await controlReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            throw new InvalidOperationException("The party control account could not be validated on the voucher.");
                        totalDebit = Convert.ToDecimal(controlReader.GetValue(0), CultureInfo.InvariantCulture);
                        totalCredit = Convert.ToDecimal(controlReader.GetValue(1), CultureInfo.InvariantCulture);
                        controlDebit = Convert.ToDecimal(controlReader.GetValue(2), CultureInfo.InvariantCulture);
                        controlCredit = Convert.ToDecimal(controlReader.GetValue(3), CultureInfo.InvariantCulture);
                    }

                    var controlAmount = linkKind == "S" ? controlDebit : controlCredit;
                    var wrongSideAmount = linkKind == "S" ? controlCredit : controlDebit;
                    var voucherAmount = linkKind == "S" ? totalDebit : totalCredit;
                    if (Math.Abs(voucherAmount - linkAmount) > 0.005m
                        || Math.Abs(controlAmount - linkAmount) > 0.005m
                        || wrongSideAmount > 0.005m)
                        throw new InvalidOperationException(
                            $"The voucher must post exactly {linkAmount:N2} to the supplier/customer control account {controlAccount} on the settlement side before it can update the party ledger.");

                    var (debit, credit) = PartyLedger.SettlementSides(linkKind, linkAmount);
                    // Step 7: the settlement inherits the voucher's own
                    // currency + header rate, so its base amount is the
                    // converted amount, never the raw foreign sum.
                    // An advance writes its party-ledger entry under the
                    // ADV kind against the advance account's settlement:
                    // the supplier statement shows the debit (prepayment)
                    // entry, and PayRecWin offers it as an advance source
                    // — never as a normal AP payment.
                    PartyLedger.WriteEntry(connection, transaction, _provider,
                        linkCompany, linkKind, linkParty, linkIsAdvance ? "ADV" : linkDocKind, null, linkDocNo,
                        entryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        debit, credit, linkCurrency ?? entryCurrency,
                        linkIsAdvance ? "Supplier advance" : "Cash voucher settlement",
                        journalEntryId.ToString("D"),
                        exchangeRate: entryRate);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return budgetWarnings;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            var recordFailure = await TryRecordBudgetBlockedAsync(connection, companyId, userId, budgetBlocked).ConfigureAwait(false);
            if (recordFailure is not null && ex is AppError appError)
                throw new AppError(appError.Code, appError.Message, recordFailure, appError.Args.ToArray());
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
            // The reversal core is shared with document cancellations
            // (GlPosting.ReverseInTransaction): identical semantics —
            // mirrored lines, source-period check, source columns copied,
            // cost splits recorded without re-validation.
            var reversalId = GlPosting.ReverseInTransaction(connection, transaction, _provider, journalEntryId, userId, details);
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
    /// Posts a source document to the GL inside its own transaction
    /// (Fix Phase Step 3): balanced entry, source-document link, real
    /// document number returned. Used by document repositories that own
    /// no transaction of their own; repositories that DO own one call
    /// GlPosting.PostInTransaction directly so document + entry commit
    /// atomically.
    /// </summary>
    public async Task<GlPostingResult> PostDocumentAsync(GlDocumentRequest request, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var budgetBlocked = new List<BudgetViolation>();
        try
        {
            var result = GlPosting.PostInTransaction(connection, transaction, _provider, request, userId, budgetBlocked);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            var recordFailure = await TryRecordBudgetBlockedAsync(connection, request.CompanyId, userId, budgetBlocked).ConfigureAwait(false);
            if (recordFailure is not null && ex is AppError appError)
                throw new AppError(appError.Code, appError.Message, recordFailure, appError.Args.ToArray());
            throw;
        }
    }

    /// <summary>Phase B 055: records BudgetGuard Blocked events after a
    /// refused posting rolled back (the events table is the audit of
    /// every block; an in-transaction row would have rolled back with
    /// the posting). Returns the recording failure, if any, instead of
    /// throwing it: the caller attaches it to the refusal's AppError as
    /// the inner exception, so a failed audit write never masks the
    /// refusal itself.</summary>
    private async Task<Exception?> TryRecordBudgetBlockedAsync(
        DbConnection connection, Guid companyId, string userId,
        List<BudgetViolation> violations)
    {
        if (violations.Count == 0 || companyId == Guid.Empty)
            return null;
        try
        {
            await using var eventsTx = await connection.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                BudgetGuard.RecordBlockedEvents(connection, eventsTx, _provider, companyId, userId, violations);
                await eventsTx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return null;
            }
            catch
            {
                await eventsTx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception recordEx)
        {
            return recordEx;
        }
    }

    /// <summary>Loads an entry's lines with account code + cost_required
    /// for the shared cost-center validation.</summary>
    private async Task<List<GlPosting.CostLine>> LoadCostLinesAsync(
        DbConnection connection, DbTransaction transaction, Guid companyId, Guid journalEntryId, CancellationToken cancellationToken)
    {
        var lines = new List<GlPosting.CostLine>();
        await using var load = CreateCommand(connection, transaction,
            "SELECT l.line_number, l.account_id, a.account_code, l.debit, l.credit, l.cost_center, COALESCE(a.cost_required, 0) FROM journal_entry_lines l LEFT JOIN chart_of_accounts a ON a.account_id = l.account_id AND a.company_id = @company WHERE l.journal_entry_id = @id ORDER BY l.line_number");
        Add(load, "@company", FormatGuid(companyId));
        Add(load, "@id", FormatGuid(journalEntryId));
        await using var reader = await load.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            lines.Add(new GlPosting.CostLine(
                Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                Guid.Parse(Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)!),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                Convert.ToBoolean(reader.GetValue(6), CultureInfo.InvariantCulture)));
        }
        return lines;
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

    /// <summary>
    /// Allocates the next number from the single company-wide journal
    /// sequence. The same transactional allocator is used by automatic
    /// document posting and reversal, so every source shares one counter.
    /// </summary>
    public string NextDocumentNumber(string companyCode, string seriesKey, DateOnly entryDate)
    {
        if (string.IsNullOrWhiteSpace(companyCode))
            throw new ArgumentException("Company code is required.", nameof(companyCode));

        var isPg = _provider == "PostgreSQL";
        using var connection = CreateConnection();
        connection.Open();
        string companyGuid;
        using (var resolve = CreateCommand(connection, null!,
            "SELECT CAST(company_id AS TEXT) FROM companies WHERE company_code = @code"))
        {
            Add(resolve, "@code", companyCode.Trim());
            companyGuid = Convert.ToString(resolve.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(companyGuid) || !Guid.TryParse(companyGuid, out var companyId))
            throw new AppError(ErrorCodes.NumberingAllocationFailed,
                $"Company '{companyCode.Trim()}' could not be resolved for journal number allocation.");

        using var transaction = connection.BeginTransaction();
        try
        {
            var number = SequenceAllocator.AllocateJournalNumber(
                connection, transaction, isPg, companyId);
            transaction.Commit();
            return number;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
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
