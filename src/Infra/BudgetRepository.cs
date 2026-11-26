using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Infrastructure;

/// <summary>
/// Provider-neutral Budgets persistence (migration 055), following the
/// JournalRepository / ProjectRepository patterns: parameterized SQL
/// only, company scoping on every statement, soft deletes, and
/// single-transaction writes with rollback.
///
/// Company keying: budgets live in the journal world, so the company is
/// the companies Guid. The two code-keyed master families the editor
/// offers (cost_centers from 031, projects from 026) are resolved
/// through the companies registry — the same dual-key reality
/// PartyCreditGuard documents: lookups accept the company's code (the
/// key those tables are written with) and, defensively, its Guid form.
/// </summary>
public sealed class BudgetRepository : IBudgetRepository
{
    private readonly string _provider;
    private readonly string _connectionString;

    public BudgetRepository(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = GlPosting.NormalizeProvider(provider);
        _connectionString = connectionString;
    }

    private bool IsPg => _provider == "PostgreSQL";
    private string NotDeleted => IsPg ? "is_deleted = false" : "is_deleted = 0";
    private string DeletedValue => IsPg ? "true" : "1";
    private string TrueValue => IsPg ? "true" : "1";
    private string NowExpr => IsPg ? "now()" : "datetime('now')";

    // ------------------------------------------------------------------
    // Reads
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<Budget>> GetAllAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var budgets = new List<Budget>();
        await using (var command = CreateCommand(connection, null,
            $"""
            SELECT b.budget_id, b.fiscal_year_id, COALESCE(CAST(fy.fiscal_year AS TEXT), ''),
                   b.name, b.budget_against, b.status, b.action_annual, b.action_monthly, b.notes
            FROM budgets b
            LEFT JOIN fiscal_years fy ON fy.fiscal_year_id = b.fiscal_year_id
            WHERE b.company_id = @company AND b.{NotDeleted}
            ORDER BY fy.fiscal_year DESC, b.name
            """))
        {
            Add(command, "@company", FormatGuid(companyId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                budgets.Add(new Budget
                {
                    Id = ReadGuid(reader, 0),
                    FiscalYearId = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                    FiscalYearLabel = ReadString(reader, 2) ?? string.Empty,
                    Name = ReadString(reader, 3) ?? string.Empty,
                    BudgetAgainst = ReadString(reader, 4) ?? BudgetAgainstKinds.CostCenter,
                    Status = ReadString(reader, 5) ?? BudgetStatuses.Draft,
                    ActionAnnual = ReadString(reader, 6) ?? BudgetActions.Stop,
                    ActionMonthly = ReadString(reader, 7) ?? BudgetActions.Warn,
                    Notes = ReadString(reader, 8)
                });
            }
        }
        foreach (var budget in budgets)
            budget.Lines = (await LoadLinesAsync(connection, companyId, budget.Id, cancellationToken).ConfigureAwait(false)).ToList();
        return budgets;
    }

    public async Task<IReadOnlyList<BudgetVarianceRow>> GetVarianceAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var budgets = await GetAllAsync(companyId, cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<BudgetVarianceRow>();
        foreach (var budget in budgets)
        {
            var throughPeriod = await CurrentPeriodNumberAsync(connection, companyId, budget.FiscalYearId, cancellationToken).ConfigureAwait(false);
            foreach (var line in budget.Lines)
            {
                var (annualActual, monthlyActual) = BudgetGuard.GetLineActuals(
                    connection, null, _provider, companyId, budget.FiscalYearId,
                    throughPeriod, line, budget.BudgetAgainst);
                var monthlyBudget = BudgetGuard.AccumulatedDistribution(
                    line.PeriodAmounts.Count > 0 ? line.PeriodAmounts : null,
                    line.AnnualAmount, throughPeriod);
                rows.Add(new BudgetVarianceRow(
                    budget.Id, budget.Name, budget.FiscalYearLabel, line.Id,
                    line.AccountCode, line.AccountName,
                    line.CostCenterCode ?? line.ProjectCode,
                    line.CostCenterCode is not null ? line.CostCenterCode : line.ProjectCode,
                    line.AnnualAmount, annualActual, monthlyBudget, monthlyActual));
            }
        }
        return rows;
    }

    public async Task<IReadOnlyList<BudgetFiscalYear>> GetFiscalYearsAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            """
            SELECT fiscal_year_id, fiscal_year, CAST(start_date AS TEXT), CAST(end_date AS TEXT)
            FROM fiscal_years WHERE company_id = @company ORDER BY fiscal_year DESC
            """);
        Add(command, "@company", FormatGuid(companyId));
        var result = new List<BudgetFiscalYear>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new BudgetFiscalYear(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty,
                ReadString(reader, 2) ?? string.Empty,
                ReadString(reader, 3) ?? string.Empty));
        }
        return result;
    }

    public async Task<IReadOnlyList<BudgetAccountOption>> GetAccountsAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var activePredicate = IsPg
            ? "is_active = true AND is_deleted = false AND is_postable = true"
            : "is_active = 1 AND is_deleted = 0 AND is_postable = 1";
        var hasBudgetFlag = PartyLedger.ColumnExists(connection, null, _provider, "chart_of_accounts", "budget");
        var flagExpr = hasBudgetFlag ? "COALESCE(budget, 0)" : "0";
        await using var command = CreateCommand(connection, null,
            $"""
            SELECT account_id, account_code, account_name, {flagExpr}
            FROM chart_of_accounts
            WHERE company_id = @company AND {activePredicate}
            ORDER BY account_code
            """);
        Add(command, "@company", FormatGuid(companyId));
        var result = new List<BudgetAccountOption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new BudgetAccountOption(
                ReadGuid(reader, 0),
                ReadString(reader, 1) ?? string.Empty,
                ReadString(reader, 2) ?? string.Empty,
                Convert.ToBoolean(reader.GetValue(3), CultureInfo.InvariantCulture)));
        }
        return result;
    }

    public async Task<IReadOnlyList<BudgetDimensionOption>> GetCostCentersAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var keys = await CompanyKeysAsync(connection, companyId, cancellationToken).ConfigureAwait(false);
        var inList = string.Join(", ", keys.Select((_, i) => $"@k{i}"));
        await using var command = CreateCommand(connection, null,
            $"""
            SELECT id, code, name_en FROM cost_centers
            WHERE company_id IN ({inList}) AND {NotDeleted} AND is_active = {TrueValue}
            ORDER BY code
            """);
        for (var i = 0; i < keys.Count; i++)
            Add(command, $"@k{i}", keys[i]);
        var result = new List<BudgetDimensionOption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new BudgetDimensionOption(
                Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty,
                ReadString(reader, 1) ?? string.Empty,
                ReadString(reader, 2) ?? string.Empty));
        }
        return result;
    }

    public async Task<IReadOnlyList<BudgetDimensionOption>> GetProjectsAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var keys = await CompanyKeysAsync(connection, companyId, cancellationToken).ConfigureAwait(false);
        var inList = string.Join(", ", keys.Select((_, i) => $"@k{i}"));
        await using var command = CreateCommand(connection, null,
            $"""
            SELECT project_id, project_code, project_name FROM projects
            WHERE company_id IN ({inList}) AND {NotDeleted}
            ORDER BY project_code
            """);
        for (var i = 0; i < keys.Count; i++)
            Add(command, $"@k{i}", keys[i]);
        var result = new List<BudgetDimensionOption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new BudgetDimensionOption(
                Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty,
                ReadString(reader, 1) ?? string.Empty,
                ReadString(reader, 2) ?? string.Empty));
        }
        return result;
    }

    // ------------------------------------------------------------------
    // Writes
    // ------------------------------------------------------------------

    public async Task SaveAsync(Guid companyId, Budget budget, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(budget);
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("User is required.", nameof(userId));

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The fiscal year must exist for this company.
            await using (var yearCheck = CreateCommand(connection, transaction,
                "SELECT COUNT(*) FROM fiscal_years WHERE company_id = @company AND fiscal_year_id = @fy"))
            {
                Add(yearCheck, "@company", FormatGuid(companyId));
                Add(yearCheck, "@fy", budget.FiscalYearId);
                if (Convert.ToInt64(await yearCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 1)
                    throw new InvalidOperationException("The selected fiscal year does not belong to this company.");
            }

            // Every line's account must be a live postable account of
            // this company; its code snapshot is refreshed from the
            // chart so reports never carry a stale code.
            foreach (var line in budget.Lines)
            {
                await using var accountCheck = CreateCommand(connection, transaction,
                    "SELECT account_code FROM chart_of_accounts WHERE company_id = @company AND account_id = @account");
                Add(accountCheck, "@company", FormatGuid(companyId));
                Add(accountCheck, "@account", FormatGuid(line.AccountId));
                var code = await accountCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (code is null or DBNull)
                    throw new InvalidOperationException($"Budget line account '{line.AccountId}' does not belong to this company.");
                line.AccountCode = Convert.ToString(code, CultureInfo.InvariantCulture)!;
                line.BudgetId = budget.Id;
            }

            var exists = false;
            await using (var existing = CreateCommand(connection, transaction,
                $"SELECT COUNT(*) FROM budgets WHERE company_id = @company AND budget_id = @id AND {NotDeleted}"))
            {
                Add(existing, "@company", FormatGuid(companyId));
                Add(existing, "@id", FormatGuid(budget.Id));
                exists = Convert.ToInt64(await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;
            }

            if (!exists)
            {
                await using var insert = CreateCommand(connection, transaction,
                    """
                    INSERT INTO budgets (budget_id, company_id, fiscal_year_id, name, budget_against,
                                         status, action_annual, action_monthly, notes, created_by)
                    VALUES (@id, @company, @fy, @name, @against, @status, @annual, @monthly, @notes, @user)
                    """);
                BindHeader(insert, companyId, budget, userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var update = CreateCommand(connection, transaction,
                    $"""
                    UPDATE budgets SET fiscal_year_id = @fy, name = @name, budget_against = @against,
                                       status = @status, action_annual = @annual, action_monthly = @monthly,
                                       notes = @notes, updated_by = @user, updated_at = {NowExpr}
                    WHERE company_id = @company AND budget_id = @id AND {NotDeleted}
                    """);
                BindHeader(update, companyId, budget, userId);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Lines + distributions are replaced wholesale inside the
            // same transaction (the editor's model), so a failed line
            // can never leave a half-saved budget behind.
            var oldIds = new List<string>();
            await using (var oldLines = CreateCommand(connection, transaction,
                "SELECT budget_line_id FROM budget_lines WHERE budget_id = @id"))
            {
                Add(oldLines, "@id", FormatGuid(budget.Id));
                await using var reader = await oldLines.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    oldIds.Add(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!);
            }
            foreach (var oldId in oldIds)
            {
                await using var deleteAmounts = CreateCommand(connection, transaction,
                    "DELETE FROM budget_period_amounts WHERE budget_line_id = @line");
                Add(deleteAmounts, "@line", IsPg ? (object)Guid.Parse(oldId) : oldId);
                await deleteAmounts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var deleteLines = CreateCommand(connection, transaction,
                "DELETE FROM budget_lines WHERE budget_id = @id"))
            {
                Add(deleteLines, "@id", FormatGuid(budget.Id));
                await deleteLines.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var line in budget.Lines)
            {
                await using var insertLine = CreateCommand(connection, transaction,
                    """
                    INSERT INTO budget_lines (budget_line_id, budget_id, account_id, account_code,
                                              cost_center_id, project_id, annual_amount)
                    VALUES (@line, @budget, @account, @code, @center, @project, @amount)
                    """);
                Add(insertLine, "@line", FormatGuid(line.Id));
                Add(insertLine, "@budget", FormatGuid(budget.Id));
                Add(insertLine, "@account", FormatGuid(line.AccountId));
                Add(insertLine, "@code", line.AccountCode.Trim());
                Add(insertLine, "@center", FormatNullableId(line.CostCenterId));
                Add(insertLine, "@project", FormatNullableId(line.ProjectId));
                Add(insertLine, "@amount", line.AnnualAmount);
                await insertLine.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                foreach (var (periodNo, amount) in line.PeriodAmounts.OrderBy(p => p.Key))
                {
                    await using var insertAmount = CreateCommand(connection, transaction,
                        """
                        INSERT INTO budget_period_amounts (budget_line_id, period_no, amount)
                        VALUES (@line, @period, @amount)
                        """);
                    Add(insertAmount, "@line", FormatGuid(line.Id));
                    Add(insertAmount, "@period", periodNo);
                    Add(insertAmount, "@amount", amount);
                    await insertAmount.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                // The chart's budget flag is the guard's opt-in marker
                // (its designed meaning): an account that gains a budget
                // line is flagged in the same transaction, so a saved
                // budget can never be silently unenforced for want of
                // the flag. Unflagging stays possible on the account
                // card as an explicit opt-out.
                if (PartyLedger.ColumnExists(connection, transaction, _provider, "chart_of_accounts", "budget"))
                {
                    await using var flag = CreateCommand(connection, transaction,
                        $"UPDATE chart_of_accounts SET budget = {TrueValue} WHERE company_id = @company AND account_id = @account");
                    Add(flag, "@company", FormatGuid(companyId));
                    Add(flag, "@account", FormatGuid(line.AccountId));
                    await flag.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task SoftDeleteAsync(Guid companyId, Guid budgetId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (budgetId == Guid.Empty) throw new ArgumentException("Budget is required.", nameof(budgetId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"UPDATE budgets SET is_deleted = {DeletedValue}, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND budget_id = @id AND {NotDeleted}");
        Add(command, "@user", userId.Trim());
        Add(command, "@company", FormatGuid(companyId));
        Add(command, "@id", FormatGuid(budgetId));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Budget was not found for this company.");
    }

    public async Task SetStatusAsync(Guid companyId, Guid budgetId, string status, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (budgetId == Guid.Empty) throw new ArgumentException("Budget is required.", nameof(budgetId));
        if (!BudgetStatuses.All.Contains(status))
            throw new InvalidOperationException($"Unknown budget status '{status}'.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"UPDATE budgets SET status = @status, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND budget_id = @id AND {NotDeleted}");
        Add(command, "@status", status);
        Add(command, "@user", userId.Trim());
        Add(command, "@company", FormatGuid(companyId));
        Add(command, "@id", FormatGuid(budgetId));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Budget was not found for this company.");
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    private async Task<IReadOnlyList<BudgetLine>> LoadLinesAsync(
        DbConnection connection, Guid companyId, Guid budgetId, CancellationToken cancellationToken)
    {
        var lines = new List<BudgetLine>();
        await using (var command = CreateCommand(connection, null,
            """
            SELECT l.budget_line_id, l.account_id, l.account_code, a.account_name,
                   l.cost_center_id, cc.code, l.project_id, p.project_code, l.annual_amount
            FROM budget_lines l
            LEFT JOIN chart_of_accounts a ON a.account_id = l.account_id AND a.company_id = @company
            LEFT JOIN cost_centers cc ON cc.id = l.cost_center_id
            LEFT JOIN projects p ON p.project_id = l.project_id
            WHERE l.budget_id = @budget ORDER BY l.account_code
            """))
        {
            Add(command, "@company", FormatGuid(companyId));
            Add(command, "@budget", FormatGuid(budgetId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                lines.Add(new BudgetLine
                {
                    Id = ReadGuid(reader, 0),
                    BudgetId = budgetId,
                    AccountId = ReadGuid(reader, 1),
                    AccountCode = ReadString(reader, 2) ?? string.Empty,
                    AccountName = ReadString(reader, 3),
                    CostCenterId = ReadNullableId(reader, 4),
                    CostCenterCode = ReadString(reader, 5),
                    ProjectId = ReadNullableId(reader, 6),
                    ProjectCode = ReadString(reader, 7),
                    AnnualAmount = ReadDecimal(reader, 8)
                });
            }
        }
        foreach (var line in lines)
        {
            await using var command = CreateCommand(connection, null,
                "SELECT period_no, amount FROM budget_period_amounts WHERE budget_line_id = @line ORDER BY period_no");
            Add(command, "@line", FormatGuid(line.Id));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                line.PeriodAmounts[Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                    Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
        }
        return lines;
    }

    /// <summary>The fiscal period number "today" falls in for the
    /// budget's fiscal year: 0 before the year starts, 12 after it
    /// ends, otherwise the containing period's number (periods are the
    /// authority, not calendar months).</summary>
    private async Task<int> CurrentPeriodNumberAsync(
        DbConnection connection, Guid companyId, long fiscalYearId, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        await using var command = CreateCommand(connection, null,
            """
            SELECT period_number, CAST(start_date AS TEXT), CAST(end_date AS TEXT)
            FROM fiscal_periods WHERE company_id = @company AND fiscal_year_id = @fy
            ORDER BY period_number
            """);
        Add(command, "@company", FormatGuid(companyId));
        Add(command, "@fy", fiscalYearId);
        var periods = new List<(int No, DateOnly Start, DateOnly End)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (DateOnly.TryParse(ReadString(reader, 1), CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) &&
                DateOnly.TryParse(ReadString(reader, 2), CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
                periods.Add((Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture), start, end));
        }
        if (periods.Count == 0)
            return 12;
        if (today < periods[0].Start)
            return 0;
        foreach (var period in periods)
            if (today >= period.Start && today <= period.End)
                return period.No;
        return periods[^1].No;
    }

    /// <summary>The company's keys in the code-keyed master world: its
    /// code (the key cost_centers/projects are written with) plus its
    /// Guid form, defensively — both name THIS company only.</summary>
    private async Task<List<string>> CompanyKeysAsync(
        DbConnection connection, Guid companyId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, null,
            "SELECT company_code FROM companies WHERE company_id = @company");
        Add(command, "@company", FormatGuid(companyId));
        var code = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var keys = new List<string>();
        if (code is not null and not DBNull)
            keys.Add(Convert.ToString(code, CultureInfo.InvariantCulture)!);
        var guidForm = companyId.ToString("D");
        if (!keys.Contains(guidForm, StringComparer.OrdinalIgnoreCase))
            keys.Add(guidForm);
        return keys;
    }

    private void BindHeader(DbCommand command, Guid companyId, Budget budget, string userId)
    {
        Add(command, "@id", FormatGuid(budget.Id));
        Add(command, "@company", FormatGuid(companyId));
        Add(command, "@fy", budget.FiscalYearId);
        Add(command, "@name", budget.Name.Trim());
        Add(command, "@against", budget.BudgetAgainst);
        Add(command, "@status", budget.Status);
        Add(command, "@annual", budget.ActionAnnual);
        Add(command, "@monthly", budget.ActionMonthly);
        Add(command, "@notes", (object?)budget.Notes ?? DBNull.Value);
        Add(command, "@user", userId.Trim());
    }

    private static void RequireCompany(Guid companyId)
    {
        if (companyId == Guid.Empty)
            throw new ArgumentException("Company is required.", nameof(companyId));
    }

    private DbConnection CreateConnection() => IsPg
        ? new NpgsqlConnection(_connectionString)
        : new SqliteConnection(_connectionString);

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        if (transaction is not null) command.Transaction = transaction;
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

    private object FormatGuid(Guid value) => IsPg ? value : value.ToString("D");

    private object FormatNullableId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return DBNull.Value;
        return IsPg ? Guid.Parse(id.Trim()) : id.Trim();
    }

    private static Guid ReadGuid(DbDataReader reader, int ordinal)
        => Guid.Parse(Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)!);

    private static string? ReadNullableId(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static string? ReadString(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static decimal ReadDecimal(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? 0m : Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
}
