using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace ShouTech.Infrastructure;

/// <summary>Creates and closes accounting structures with checks shared by SQLite and PostgreSQL.</summary>
public sealed class AccountingStructureRepository
{
    private readonly string _provider;
    private readonly string _connectionString;

    public AccountingStructureRepository(string provider, string connectionString)
    {
        _provider = provider.Trim().ToLowerInvariant() switch
        {
            "sqlite" or "sqlite3" => "SQLite",
            "postgres" or "postgresql" or "npgsql" => "PostgreSQL",
            _ => throw new NotSupportedException("Only SQLite and PostgreSQL are supported.")
        };
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("A database connection string is required.", nameof(connectionString))
            : connectionString;
    }

    public async Task<long> CreateFiscalYearAsync(Guid companyId, int year, string actor, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        if (year is < 1900 or > 9998) throw new ArgumentOutOfRangeException(nameof(year));
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("Actor is required.", nameof(actor));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var yearCommand = CreateCommand(connection, transaction,
                _provider == "PostgreSQL"
                ? "INSERT INTO fiscal_years(company_id,fiscal_year,database_name,start_date,end_date,is_current,is_closed) VALUES(@company,@year,'',@start,@end,false,false) RETURNING fiscal_year_id"
                : "INSERT INTO fiscal_years(company_id,fiscal_year,database_name,start_date,end_date,is_current,is_closed) VALUES(@company,@year,'',@start,@end,0,0)");
            Add(yearCommand, "@company", CompanyValue(companyId));
            Add(yearCommand, "@year", year);
            Add(yearCommand, "@start", DateValue(new DateOnly(year, 1, 1)));
            Add(yearCommand, "@end", DateValue(new DateOnly(year, 12, 31)));
            long fiscalYearId;
            if (_provider == "PostgreSQL")
                fiscalYearId = Convert.ToInt64(await yearCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            else
            {
                await yearCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await using var idCommand = CreateCommand(connection, transaction, "SELECT last_insert_rowid()");
                fiscalYearId = Convert.ToInt64(await idCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }

            for (var month = 1; month <= 12; month++)
            {
                var start = new DateOnly(year, month, 1);
                var end = start.AddMonths(1).AddDays(-1);
                await using var periodCommand = CreateCommand(connection, transaction,
                    "INSERT INTO fiscal_periods(fiscal_period_id,company_id,fiscal_year_id,period_number,period_name,start_date,end_date,status) VALUES(@id,@company,@yearId,@number,@name,@start,@end,'OPEN')");
                Add(periodCommand, "@id", PeriodIdValue(Guid.NewGuid()));
                Add(periodCommand, "@company", CompanyValue(companyId));
                Add(periodCommand, "@yearId", fiscalYearId);
                Add(periodCommand, "@number", month);
                Add(periodCommand, "@name", $"{year:D4}-{month:D2}");
                Add(periodCommand, "@start", DateValue(start));
                Add(periodCommand, "@end", DateValue(end));
                await periodCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return fiscalYearId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<Guid> CreateAccountAsync(Guid companyId, string code, string name, string accountType, bool isPostable = true, Guid? parentAccountId = null, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Account code and name are required.");
        var type = accountType.Trim().ToUpperInvariant();
        if (type is not ("ASSET" or "LIABILITY" or "EQUITY" or "REVENUE" or "EXPENSE"))
            throw new ArgumentException("Account type must be ASSET, LIABILITY, EQUITY, REVENUE, or EXPENSE.", nameof(accountType));
        var accountId = Guid.NewGuid();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (parentAccountId.HasValue)
            {
                await using var parent = CreateCommand(connection, transaction,
                    "SELECT COUNT(*) FROM chart_of_accounts WHERE account_id=@parent AND company_id=@company AND is_active=" + (_provider == "PostgreSQL" ? "true" : "1") + " AND is_deleted=" + (_provider == "PostgreSQL" ? "false" : "0") + " AND is_postable=" + (_provider == "PostgreSQL" ? "false" : "0"));
                Add(parent, "@parent", AccountIdValue(parentAccountId.Value));
                Add(parent, "@company", CompanyValue(companyId));
                if (Convert.ToInt64(await parent.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 1)
                    throw new InvalidOperationException("Parent account must be an active, non-postable account in the same company.");
            }
            await using var command = CreateCommand(connection, transaction,
                "INSERT INTO chart_of_accounts(account_id,company_id,account_code,account_name,account_type,parent_account_id,is_postable,is_active,is_deleted) VALUES(@id,@company,@code,@name,@type,@parent,@postable,@active,@deleted)");
            Add(command, "@id", AccountIdValue(accountId));
            Add(command, "@company", CompanyValue(companyId));
            Add(command, "@code", code.Trim());
            Add(command, "@name", name.Trim());
            Add(command, "@type", type);
            Add(command, "@parent", parentAccountId.HasValue ? AccountIdValue(parentAccountId.Value) : DBNull.Value);
            Add(command, "@postable", BoolValue(isPostable));
            Add(command, "@active", BoolValue(true));
            Add(command, "@deleted", BoolValue(false));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return accountId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task ClosePeriodAsync(Guid fiscalPeriodId, string actor, CancellationToken cancellationToken = default)
    {
        if (fiscalPeriodId == Guid.Empty) throw new ArgumentException("Fiscal period is required.", nameof(fiscalPeriodId));
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("Actor is required.", nameof(actor));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var periodParam = PeriodIdValue(fiscalPeriodId);
            await EnsureNoDraftsAsync(connection, transaction, "e.fiscal_period_id=@period", periodParam, cancellationToken).ConfigureAwait(false);
            await EnsurePostedJournalsBalancedAsync(connection, transaction, "e.fiscal_period_id=@period", periodParam, cancellationToken).ConfigureAwait(false);
            // Enforce sequential close: earlier periods must already be closed/locked.
            await using (var order = CreateCommand(connection, transaction,
                "SELECT COUNT(*) FROM fiscal_periods target JOIN fiscal_periods earlier ON earlier.company_id=target.company_id AND earlier.start_date<target.start_date WHERE target.fiscal_period_id=@period AND earlier.status='OPEN'"))
            {
                Add(order, "@period", periodParam);
                if (Convert.ToInt64(await order.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0)
                    throw new InvalidOperationException("Close fiscal periods in chronological order; an earlier period is still open.");
            }
            await using var update = CreateCommand(connection, transaction,
                "UPDATE fiscal_periods SET status='CLOSED',closed_by=@actor,closed_at=CURRENT_TIMESTAMP WHERE fiscal_period_id=@period AND status='OPEN'");
            Add(update, "@actor", actor.Trim());
            Add(update, "@period", periodParam);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Fiscal period was not found or is already closed/locked.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
    }

    public async Task CloseFiscalYearAsync(long fiscalYearId, string actor, CancellationToken cancellationToken = default)
    {
        if (fiscalYearId <= 0) throw new ArgumentOutOfRangeException(nameof(fiscalYearId));
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("Actor is required.", nameof(actor));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var periods = CreateCommand(connection, transaction,
                "SELECT COUNT(*), SUM(CASE WHEN status='OPEN' THEN 1 ELSE 0 END) FROM fiscal_periods WHERE fiscal_year_id=@year"))
            {
                Add(periods, "@year", fiscalYearId);
                await using var reader = await periods.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture) != 12 || Convert.ToInt32(reader.IsDBNull(1) ? 0 : reader.GetValue(1), CultureInfo.InvariantCulture) != 0)
                    throw new InvalidOperationException("Fiscal year requires all 12 monthly periods to be closed or locked before year-end closure.");
            }
            await EnsureNoDraftsAsync(connection, transaction, "e.company_id=fy.company_id AND e.entry_date BETWEEN fy.start_date AND fy.end_date", null, cancellationToken, fiscalYearId).ConfigureAwait(false);
            await EnsurePostedJournalsBalancedAsync(connection, transaction, "e.company_id=fy.company_id AND e.entry_date BETWEEN fy.start_date AND fy.end_date", null, cancellationToken, fiscalYearId).ConfigureAwait(false);
            // Require revenue and expense balances to be cleared by a posted closing entry before locking the year.
            var zeroPredicate = _provider == "PostgreSQL" ? "a.is_active=true AND a.is_deleted=false" : "a.is_active=1 AND a.is_deleted=0";
            await using (var pnl = CreateCommand(connection, transaction,
                $"SELECT COUNT(*) FROM (SELECT a.account_id, SUM(l.debit-l.credit) AS balance FROM fiscal_years fy JOIN journal_entries e ON e.company_id=fy.company_id AND e.entry_date BETWEEN fy.start_date AND fy.end_date AND e.status=1 JOIN journal_entry_lines l ON l.journal_entry_id=e.journal_entry_id JOIN chart_of_accounts a ON a.account_id=l.account_id AND a.company_id=e.company_id WHERE fy.fiscal_year_id=@year AND {zeroPredicate} AND a.account_type IN ('REVENUE','EXPENSE') GROUP BY a.account_id HAVING ABS(SUM(l.debit-l.credit)) > 0.0001) unclosed"))
            {
                Add(pnl, "@year", fiscalYearId);
                if (Convert.ToInt64(await pnl.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
                    throw new InvalidOperationException("Year cannot be closed: revenue/expense accounts still have balances. Post the approved income-statement closing entry first.");
            }
            await using var update = CreateCommand(connection, transaction,
                "UPDATE fiscal_years SET is_closed=" + (_provider == "PostgreSQL" ? "true" : "1") + ",closed_by=@actor,closed_at=CURRENT_TIMESTAMP WHERE fiscal_year_id=@year AND is_closed=" + (_provider == "PostgreSQL" ? "false" : "0"));
            Add(update, "@actor", actor.Trim());
            Add(update, "@year", fiscalYearId);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Fiscal year was not found or is already closed.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
    }

    private async Task EnsureNoDraftsAsync(DbConnection c, DbTransaction t, string predicate, object? period, CancellationToken ct, long? year = null)
    {
        var sql = year.HasValue
            ? "SELECT COUNT(*) FROM journal_entries e JOIN fiscal_years fy ON " + predicate + " WHERE fy.fiscal_year_id=@year AND e.status=0"
            : "SELECT COUNT(*) FROM journal_entries e WHERE " + predicate + " AND e.status=0";
        await using var cmd = CreateCommand(c,t,sql);
        if (year.HasValue) Add(cmd,"@year",year.Value); else Add(cmd,"@period",period ?? DBNull.Value);
        if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
            throw new InvalidOperationException("Cannot close an accounting period/year while draft journal entries remain.");
    }

    private async Task EnsurePostedJournalsBalancedAsync(DbConnection c, DbTransaction t, string predicate, object? period, CancellationToken ct, long? year = null)
    {
        var sql = year.HasValue
            ? "SELECT COUNT(*) FROM (SELECT e.journal_entry_id, SUM(l.debit) d, SUM(l.credit) cr FROM journal_entries e JOIN fiscal_years fy ON " + predicate + " JOIN journal_entry_lines l ON l.journal_entry_id=e.journal_entry_id WHERE fy.fiscal_year_id=@year AND e.status IN (1,2) GROUP BY e.journal_entry_id HAVING COUNT(l.journal_entry_line_id)<2 OR ABS(COALESCE(SUM(l.debit),0)-COALESCE(SUM(l.credit),0))>0.0001) x"
            : "SELECT COUNT(*) FROM (SELECT e.journal_entry_id FROM journal_entries e JOIN journal_entry_lines l ON l.journal_entry_id=e.journal_entry_id WHERE " + predicate + " AND e.status IN (1,2) GROUP BY e.journal_entry_id HAVING COUNT(l.journal_entry_line_id)<2 OR ABS(COALESCE(SUM(l.debit),0)-COALESCE(SUM(l.credit),0))>0.0001) x";
        await using var cmd = CreateCommand(c,t,sql);
        if (year.HasValue) Add(cmd,"@year",year.Value); else Add(cmd,"@period",period ?? DBNull.Value);
        if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
            throw new InvalidOperationException("Cannot close accounting period/year: one or more posted journals are unbalanced.");
    }

    private DbConnection CreateConnection() => _provider == "SQLite" ? new SqliteConnection(_connectionString) : new NpgsqlConnection(_connectionString);
    private static DbCommand CreateCommand(DbConnection c, DbTransaction t, string sql) { var cmd=c.CreateCommand(); cmd.Transaction=t; cmd.CommandText=sql; return cmd; }
    private static void Add(DbCommand cmd,string name,object value) { var p=cmd.CreateParameter(); p.ParameterName=name; p.Value=value; cmd.Parameters.Add(p); }
    private object CompanyValue(Guid id) => _provider == "PostgreSQL" ? id : id.ToString();
    private object AccountIdValue(Guid id) => _provider == "PostgreSQL" ? id : id.ToString();
    private object PeriodIdValue(Guid id) => _provider == "PostgreSQL" ? id : id.ToString();
    private object DateValue(DateOnly date) => _provider == "PostgreSQL" ? date : date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
    private object BoolValue(bool value) => _provider == "PostgreSQL" ? value : (value ? 1 : 0);
}
