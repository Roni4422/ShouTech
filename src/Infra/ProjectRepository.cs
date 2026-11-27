using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Infrastructure;

/// <summary>
/// Provider-neutral Projects persistence (SQLite + PostgreSQL), following the
/// JournalRepository patterns: parameterized SQL only, company scoping on every
/// statement, soft deletes, and single-transaction writes with rollback.
/// </summary>
public sealed class ProjectRepository : IProjectRepository
{
    private const int ListLimit = 500;

    private readonly string _provider;
    private readonly string _connectionString;

    public ProjectRepository(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = NormalizeProvider(provider);
        _connectionString = connectionString;
    }

    private string NotDeleted => _provider == "PostgreSQL" ? "is_deleted = false" : "is_deleted = 0";
    private string DeletedValue => _provider == "PostgreSQL" ? "true" : "1";
    private string NowExpr => _provider == "PostgreSQL" ? "now()" : "datetime('now')";

    public async Task<IReadOnlyList<Project>> GetAllAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT project_id, project_code, project_name, customer_id, customer_name, status, start_date, end_date, budget, notes FROM projects WHERE company_id = @company AND {NotDeleted} ORDER BY project_code LIMIT {ListLimit}");
        Add(command, "@company", companyId.Trim());
        var result = new List<Project>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new Project
            {
                Id = ReadGuid(reader, 0),
                Code = ReadString(reader, 1) ?? string.Empty,
                Name = ReadString(reader, 2) ?? string.Empty,
                CustomerId = ReadNullableLong(reader, 3),
                CustomerName = ReadString(reader, 4),
                Status = ReadString(reader, 5) ?? ProjectStatuses.Planned,
                StartDate = ReadDate(reader, 6),
                EndDate = ReadDate(reader, 7),
                Budget = ReadDecimal(reader, 8),
                Notes = ReadString(reader, 9)
            });
        }
        return result;
    }

    public async Task SaveAsync(string companyId, Project project, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(project);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Company-scoped code uniqueness among live projects (also enforced
            // by the ux_projects_company_code partial unique index).
            await using (var dup = CreateCommand(connection, transaction,
                $"SELECT COUNT(*) FROM projects WHERE company_id = @company AND project_code = @code AND {NotDeleted} AND project_id <> @id"))
            {
                Add(dup, "@company", companyId.Trim());
                Add(dup, "@code", project.Code.Trim());
                Add(dup, "@id", FormatGuid(project.Id));
                if (Convert.ToInt64(await dup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
                    throw new InvalidOperationException($"Project code '{project.Code.Trim()}' already exists for this company.");
            }

            long exists;
            await using (var check = CreateCommand(connection, transaction,
                "SELECT COUNT(*) FROM projects WHERE company_id = @company AND project_id = @id"))
            {
                Add(check, "@company", companyId.Trim());
                Add(check, "@id", FormatGuid(project.Id));
                exists = Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }

            if (exists == 0)
            {
                await using var insert = CreateCommand(connection, transaction,
                    "INSERT INTO projects (project_id, company_id, project_code, project_name, customer_id, customer_name, status, start_date, end_date, budget, notes, created_by) VALUES (@id, @company, @code, @name, @customer, @customerName, @status, @start, @end, @budget, @notes, @user)");
                BindProject(insert, companyId, project, userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var update = CreateCommand(connection, transaction,
                    $"UPDATE projects SET project_code = @code, project_name = @name, customer_id = @customer, customer_name = @customerName, status = @status, start_date = @start, end_date = @end, budget = @budget, notes = @notes, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND project_id = @id AND {NotDeleted}");
                BindProject(update, companyId, project, userId);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Project could not be updated (missing or deleted).");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task SoftDeleteAsync(string companyId, Guid projectId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"UPDATE projects SET is_deleted = {DeletedValue}, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND project_id = @id AND {NotDeleted}");
        Add(command, "@company", companyId.Trim());
        Add(command, "@id", FormatGuid(projectId));
        Add(command, "@user", userId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Project was not found or is already deleted.");
    }

    public async Task<IReadOnlyList<ProjectTask>> GetTasksAsync(string companyId, Guid projectId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT task_id, project_id, task_name, status, due_date, assigned_to, notes FROM project_tasks WHERE company_id = @company AND project_id = @project AND {NotDeleted} ORDER BY due_date, created_at LIMIT {ListLimit}");
        Add(command, "@company", companyId.Trim());
        Add(command, "@project", FormatGuid(projectId));
        var result = new List<ProjectTask>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ProjectTask
            {
                Id = ReadGuid(reader, 0),
                ProjectId = ReadGuid(reader, 1),
                Name = ReadString(reader, 2) ?? string.Empty,
                Status = ReadString(reader, 3) ?? ProjectTaskStatuses.ToDo,
                DueDate = ReadDate(reader, 4),
                AssignedTo = ReadString(reader, 5),
                Notes = ReadString(reader, 6)
            });
        }
        return result;
    }

    public async Task SaveTaskAsync(string companyId, ProjectTask task, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(task);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The parent project must exist, belong to the company, and be live.
            await using (var parent = CreateCommand(connection, transaction,
                $"SELECT COUNT(*) FROM projects WHERE company_id = @company AND project_id = @project AND {NotDeleted}"))
            {
                Add(parent, "@company", companyId.Trim());
                Add(parent, "@project", FormatGuid(task.ProjectId));
                if (Convert.ToInt64(await parent.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 1)
                    throw new InvalidOperationException("The task's project was not found for this company.");
            }

            long exists;
            await using (var check = CreateCommand(connection, transaction,
                "SELECT COUNT(*) FROM project_tasks WHERE company_id = @company AND task_id = @id"))
            {
                Add(check, "@company", companyId.Trim());
                Add(check, "@id", FormatGuid(task.Id));
                exists = Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }

            if (exists == 0)
            {
                await using var insert = CreateCommand(connection, transaction,
                    "INSERT INTO project_tasks (task_id, company_id, project_id, task_name, status, due_date, assigned_to, notes, created_by) VALUES (@id, @company, @project, @name, @status, @due, @assigned, @notes, @user)");
                BindTask(insert, companyId, task, userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var update = CreateCommand(connection, transaction,
                    $"UPDATE project_tasks SET task_name = @name, status = @status, due_date = @due, assigned_to = @assigned, notes = @notes, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND task_id = @id AND project_id = @project AND {NotDeleted}");
                BindTask(update, companyId, task, userId);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Task could not be updated (missing or deleted).");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task SoftDeleteTaskAsync(string companyId, Guid taskId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"UPDATE project_tasks SET is_deleted = {DeletedValue}, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND task_id = @id AND {NotDeleted}");
        Add(command, "@company", companyId.Trim());
        Add(command, "@id", FormatGuid(taskId));
        Add(command, "@user", userId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Task was not found or is already deleted.");
    }

    private void BindProject(DbCommand command, string companyId, Project project, string userId)
    {
        Add(command, "@id", FormatGuid(project.Id));
        Add(command, "@company", companyId.Trim());
        Add(command, "@code", project.Code.Trim());
        Add(command, "@name", project.Name.Trim());
        Add(command, "@customer", (object?)project.CustomerId ?? DBNull.Value);
        Add(command, "@customerName", (object?)NullIfBlank(project.CustomerName) ?? DBNull.Value);
        Add(command, "@status", project.Status);
        Add(command, "@start", (object?)FormatDate(project.StartDate) ?? DBNull.Value);
        Add(command, "@end", (object?)FormatDate(project.EndDate) ?? DBNull.Value);
        Add(command, "@budget", project.Budget);
        Add(command, "@notes", (object?)NullIfBlank(project.Notes) ?? DBNull.Value);
        Add(command, "@user", userId);
    }

    private void BindTask(DbCommand command, string companyId, ProjectTask task, string userId)
    {
        Add(command, "@id", FormatGuid(task.Id));
        Add(command, "@company", companyId.Trim());
        Add(command, "@project", FormatGuid(task.ProjectId));
        Add(command, "@name", task.Name.Trim());
        Add(command, "@status", task.Status);
        Add(command, "@due", (object?)FormatDate(task.DueDate) ?? DBNull.Value);
        Add(command, "@assigned", (object?)NullIfBlank(task.AssignedTo) ?? DBNull.Value);
        Add(command, "@notes", (object?)NullIfBlank(task.Notes) ?? DBNull.Value);
        Add(command, "@user", userId);
    }

    private static void RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? FormatDate(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private DbConnection CreateConnection() => _provider == "SQLite"
        ? new SqliteConnection(_connectionString)
        : new NpgsqlConnection(_connectionString);

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

    private object FormatGuid(Guid value) => _provider == "PostgreSQL" ? value : value.ToString();

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

    private static long? ReadNullableLong(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static decimal ReadDecimal(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? 0m : Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

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
