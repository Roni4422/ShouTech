using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Infrastructure;

/// <summary>
/// Provider-neutral persistence for the recurring sales engine
/// (migration 106), following the CommRepository patterns: parameterized
/// SQL only, company scoping on every statement, soft deletes,
/// single-transaction writes. Company keying is the company CODE.
///
/// IDEMPOTENCY: generation claims an occurrence BEFORE cloning —
/// ClaimOccurrenceAsync inserts the run row and advances the schedule in
/// one transaction; the ux_sre_runs_schedule_occurrence unique index
/// makes a second claim of the same occurrence a no-op (null return), so
/// re-running a generation pass for the same date creates nothing new.
/// </summary>
public sealed class RecurRepository : IRecurRepository
{
    private readonly string _provider;
    private readonly string _connectionString;

    public RecurRepository(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = GlPosting.NormalizeProvider(provider);
        _connectionString = connectionString;
    }

    private bool IsPg => _provider == "PostgreSQL";
    private string NotDeleted => IsPg ? "is_deleted = false" : "is_deleted = 0";
    private string NowExpr => IsPg ? "now()" : "datetime('now')";
    private object Truth(bool value) => IsPg ? value : (value ? 1 : 0);

    private const string ScheduleColumns = """
        schedule_id, company_id, template_doc_id, template_doc_kind, template_doc_no,
        interval_type, interval_count, start_date, next_run_date, end_date, max_occurrences,
        occurrences_created, auto_post, status, last_run_date, last_run_status, last_generated_doc_id
        """;

    // ------------------------------------------------------------------
    // Schedules
    // ------------------------------------------------------------------
    public async Task<IReadOnlyList<RecurSchedule>> GetSchedulesAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            SELECT {ScheduleColumns} FROM sre_recurring_schedules
            WHERE company_id = @c AND {NotDeleted}
            ORDER BY next_run_date, template_doc_no
            """);
        Add(command, "@c", companyId.Trim());
        var schedules = new List<RecurSchedule>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            schedules.Add(ReadSchedule(reader));
        return schedules;
    }

    public async Task<RecurSchedule?> GetScheduleAsync(string companyId, Guid scheduleId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadScheduleAsync(connection, null, companyId.Trim(), scheduleId, cancellationToken).ConfigureAwait(false);
    }

    public async Task InsertScheduleAsync(string companyId, RecurSchedule schedule, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(schedule);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, """
            INSERT INTO sre_recurring_schedules
                (schedule_id, company_id, template_doc_id, template_doc_kind, template_doc_no,
                 interval_type, interval_count, start_date, next_run_date, end_date, max_occurrences,
                 occurrences_created, auto_post, status, created_by)
            VALUES (@id, @c, @doc, @kind, @docNo, @interval, @count, @start, @next, @end, @max,
                    0, @autoPost, 'Active', @user)
            """);
        Add(command, "@id", schedule.Id.ToString("D"));
        Add(command, "@c", companyId.Trim());
        Add(command, "@doc", schedule.TemplateDocId);
        Add(command, "@kind", schedule.TemplateDocKind);
        Add(command, "@docNo", schedule.TemplateDocNo);
        Add(command, "@interval", schedule.IntervalType);
        Add(command, "@count", schedule.IntervalCount);
        Add(command, "@start", FormatDate(schedule.StartDate));
        Add(command, "@next", FormatDate(schedule.NextRunDate));
        Add(command, "@end", (object?)FormatDate(schedule.EndDate) ?? DBNull.Value);
        Add(command, "@max", (object?)schedule.MaxOccurrences ?? DBNull.Value);
        Add(command, "@autoPost", Truth(schedule.AutoPost));
        Add(command, "@user", userId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetStatusAsync(string companyId, Guid scheduleId, string fromStatus, string toStatus, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            UPDATE sre_recurring_schedules SET status = @to, updated_by = @user, updated_at = {NowExpr}
            WHERE company_id = @c AND schedule_id = @id AND status = @from AND {NotDeleted}
            """);
        Add(command, "@to", toStatus);
        Add(command, "@user", userId);
        Add(command, "@c", companyId.Trim());
        Add(command, "@id", scheduleId.ToString("D"));
        Add(command, "@from", fromStatus);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
            throw new InvalidOperationException(
                $"Recurring schedule '{scheduleId}' is not in status '{fromStatus}' and cannot move to '{toStatus}'.");
    }

    public async Task<IReadOnlyList<RecurRun>> GetRunsAsync(string companyId, Guid scheduleId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, """
            SELECT run_id, schedule_id, occurrence_no, run_date, generated_doc_id, outcome, error_text
            FROM sre_recurring_runs
            WHERE company_id = @c AND schedule_id = @id
            ORDER BY occurrence_no
            """);
        Add(command, "@c", companyId.Trim());
        Add(command, "@id", scheduleId.ToString("D"));
        var runs = new List<RecurRun>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            runs.Add(new RecurRun(
                Guid.Parse(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!),
                Guid.Parse(Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)!),
                Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
                ParseDate(Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture))!.Value,
                reader.IsDBNull(4) ? null : Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
                Convert.ToString(reader.GetValue(5), CultureInfo.InvariantCulture)!,
                reader.IsDBNull(6) ? null : Convert.ToString(reader.GetValue(6), CultureInfo.InvariantCulture)));
        }
        return runs;
    }

    // ------------------------------------------------------------------
    // Generation bookkeeping
    // ------------------------------------------------------------------
    public async Task<IReadOnlyList<(string CompanyId, RecurSchedule Schedule)>> GetDueSchedulesAsync(DateOnly asOf, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            SELECT {ScheduleColumns} FROM sre_recurring_schedules
            WHERE status = 'Active' AND next_run_date <= @asOf AND {NotDeleted}
            ORDER BY company_id, next_run_date
            """);
        Add(command, "@asOf", FormatDate(asOf));
        var due = new List<(string, RecurSchedule)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schedule = ReadSchedule(reader);
            due.Add((Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)!, schedule));
        }
        return due;
    }

    public async Task<Guid?> ClaimOccurrenceAsync(string companyId, Guid scheduleId, int occurrenceNo, DateOnly runDate,
        DateOnly? newNextRunDate, bool completesSchedule, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var company = companyId.Trim();
        var runId = Guid.NewGuid();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The claim: the unique (schedule_id, occurrence_no) index is
            // the guard — a duplicate claim inserts nothing.
            var insertSql = IsPg
                ? "INSERT INTO sre_recurring_runs (run_id, schedule_id, company_id, occurrence_no, run_date, outcome, created_by) VALUES (@run, @id, @c, @no, @date, 'Pending', @user) ON CONFLICT (schedule_id, occurrence_no) DO NOTHING"
                : "INSERT OR IGNORE INTO sre_recurring_runs (run_id, schedule_id, company_id, occurrence_no, run_date, outcome, created_by) VALUES (@run, @id, @c, @no, @date, 'Pending', @user)";
            await using (var insert = CreateCommand(connection, transaction, insertSql))
            {
                Add(insert, "@run", runId.ToString("D"));
                Add(insert, "@id", scheduleId.ToString("D"));
                Add(insert, "@c", company);
                Add(insert, "@no", occurrenceNo);
                Add(insert, "@date", FormatDate(runDate));
                Add(insert, "@user", userId);
                var inserted = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (inserted == 0)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return null; // already claimed by an earlier/concurrent pass
                }
            }

            // The advance rides in the SAME transaction as the claim, so a
            // claimed occurrence can never be re-claimed or lost silently:
            // the schedule always points at the NEXT unclaimed occurrence.
            await using (var advance = CreateCommand(connection, transaction, $"""
                UPDATE sre_recurring_schedules
                SET occurrences_created = @no,
                    next_run_date = @next,
                    status = @status,
                    last_run_date = @date,
                    updated_by = @user, updated_at = {NowExpr}
                WHERE company_id = @c AND schedule_id = @id AND status = 'Active' AND {NotDeleted}
                """))
            {
                Add(advance, "@no", occurrenceNo);
                Add(advance, "@next", (object?)FormatDate(newNextRunDate) ?? DBNull.Value);
                Add(advance, "@status", completesSchedule ? RecurScheduleStatus.Completed : RecurScheduleStatus.Active);
                Add(advance, "@date", FormatDate(runDate));
                Add(advance, "@user", userId);
                Add(advance, "@c", company);
                Add(advance, "@id", scheduleId.ToString("D"));
                var advanced = await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (advanced != 1)
                    throw new InvalidOperationException(
                        $"Recurring schedule '{scheduleId}' is no longer Active; occurrence {occurrenceNo} cannot be claimed.");
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return runId;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task CompleteOccurrenceAsync(string companyId, Guid runId, long generatedDocId, string outcome, string? errorText, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var company = companyId.Trim();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Guid scheduleId;
            await using (var read = CreateCommand(connection, transaction,
                "SELECT schedule_id FROM sre_recurring_runs WHERE company_id = @c AND run_id = @run"))
            {
                Add(read, "@c", company);
                Add(read, "@run", runId.ToString("D"));
                var value = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (value is null or DBNull)
                    throw new InvalidOperationException($"Recurring run '{runId}' was not found.");
                scheduleId = Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
            }

            await using (var finish = CreateCommand(connection, transaction, """
                UPDATE sre_recurring_runs SET generated_doc_id = @doc, outcome = @outcome,
                    error_text = @error, completed_at = @now
                WHERE company_id = @c AND run_id = @run
                """))
            {
                Add(finish, "@doc", generatedDocId);
                Add(finish, "@outcome", outcome);
                Add(finish, "@error", (object?)errorText ?? DBNull.Value);
                Add(finish, "@now", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                Add(finish, "@c", company);
                Add(finish, "@run", runId.ToString("D"));
                await finish.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Stamp the generated document with its schedule (the primary
            // source link; the service also writes the document_links row).
            if (PartyLedger.ColumnExists(connection, transaction, _provider, "sales_documents", "recurring_schedule_id"))
            {
                await using var stamp = CreateCommand(connection, transaction,
                    "UPDATE sales_documents SET recurring_schedule_id = @schedule WHERE doc_id = @doc");
                Add(stamp, "@schedule", scheduleId.ToString("D"));
                Add(stamp, "@doc", generatedDocId);
                await stamp.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var mark = CreateCommand(connection, transaction, $"""
                UPDATE sre_recurring_schedules SET last_run_status = @status, last_generated_doc_id = @doc,
                    updated_by = @user, updated_at = {NowExpr}
                WHERE company_id = @c AND schedule_id = @id AND {NotDeleted}
                """))
            {
                Add(mark, "@status", errorText is null ? outcome : $"{outcome}: {errorText}");
                Add(mark, "@doc", generatedDocId);
                Add(mark, "@user", userId);
                Add(mark, "@c", company);
                Add(mark, "@id", scheduleId.ToString("D"));
                await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task FailOccurrenceAsync(string companyId, Guid runId, string errorText, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var company = companyId.Trim();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Guid scheduleId;
            await using (var read = CreateCommand(connection, transaction,
                "SELECT schedule_id FROM sre_recurring_runs WHERE company_id = @c AND run_id = @run"))
            {
                Add(read, "@c", company);
                Add(read, "@run", runId.ToString("D"));
                var value = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (value is null or DBNull)
                    throw new InvalidOperationException($"Recurring run '{runId}' was not found.");
                scheduleId = Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
            }

            await using (var finish = CreateCommand(connection, transaction, """
                UPDATE sre_recurring_runs SET outcome = 'Failed', error_text = @error, completed_at = @now
                WHERE company_id = @c AND run_id = @run
                """))
            {
                Add(finish, "@error", errorText);
                Add(finish, "@now", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                Add(finish, "@c", company);
                Add(finish, "@run", runId.ToString("D"));
                await finish.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var mark = CreateCommand(connection, transaction, $"""
                UPDATE sre_recurring_schedules SET last_run_status = @status,
                    updated_by = @user, updated_at = {NowExpr}
                WHERE company_id = @c AND schedule_id = @id AND {NotDeleted}
                """))
            {
                Add(mark, "@status", $"{RecurRunOutcome.Failed}: {errorText}");
                Add(mark, "@user", userId);
                Add(mark, "@c", company);
                Add(mark, "@id", scheduleId.ToString("D"));
                await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<string> ResolveCompanyCodeAsync(string companyKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(companyKey))
            throw new ArgumentException("Company is required.", nameof(companyKey));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await PartyLedger.ResolveCompanyCodeAsync(connection, null, _provider, companyKey.Trim(), cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------
    private async Task<RecurSchedule?> ReadScheduleAsync(DbConnection connection, DbTransaction? transaction, string company, Guid scheduleId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, $"""
            SELECT {ScheduleColumns} FROM sre_recurring_schedules
            WHERE company_id = @c AND schedule_id = @id AND {NotDeleted}
            """);
        Add(command, "@c", company);
        Add(command, "@id", scheduleId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSchedule(reader) : null;
    }

    private static RecurSchedule ReadSchedule(DbDataReader reader) => new()
    {
        Id = Guid.Parse(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!),
        TemplateDocId = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
        TemplateDocKind = Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture)!,
        TemplateDocNo = Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture) ?? string.Empty,
        IntervalType = Convert.ToString(reader.GetValue(5), CultureInfo.InvariantCulture)!,
        IntervalCount = Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture),
        StartDate = ParseDate(Convert.ToString(reader.GetValue(7), CultureInfo.InvariantCulture))!.Value,
        NextRunDate = ParseDate(Convert.ToString(reader.GetValue(8), CultureInfo.InvariantCulture))!.Value,
        EndDate = reader.IsDBNull(9) ? null : ParseDate(Convert.ToString(reader.GetValue(9), CultureInfo.InvariantCulture)),
        MaxOccurrences = reader.IsDBNull(10) ? null : Convert.ToInt32(reader.GetValue(10), CultureInfo.InvariantCulture),
        OccurrencesCreated = Convert.ToInt32(reader.GetValue(11), CultureInfo.InvariantCulture),
        AutoPost = Convert.ToBoolean(reader.GetValue(12), CultureInfo.InvariantCulture),
        Status = Convert.ToString(reader.GetValue(13), CultureInfo.InvariantCulture)!,
        LastRunDate = reader.IsDBNull(14) ? null : ParseDate(Convert.ToString(reader.GetValue(14), CultureInfo.InvariantCulture)),
        LastRunStatus = reader.IsDBNull(15) ? null : Convert.ToString(reader.GetValue(15), CultureInfo.InvariantCulture),
        LastGeneratedDocId = reader.IsDBNull(16) ? null : Convert.ToInt64(reader.GetValue(16), CultureInfo.InvariantCulture)
    };

    private static string FormatDate(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string? FormatDate(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly? ParseDate(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : DateOnly.ParseExact(value.Trim()[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static void RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
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
}
