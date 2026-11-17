using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Infrastructure;

/// <summary>
/// Provider-neutral Definition persistence (SQLite + PostgreSQL): parameterized
/// SQL only, company scoping on every statement, single-transaction writes.
///
/// Identifier safety (Phase 4 SqlIdentifier pattern): table/column names are
/// interpolated ONLY from the static <see cref="DefSets"/> descriptors, and
/// every interpolated identifier is additionally validated against the
/// descriptor allow-list plus a strict shape regex before it reaches SQL.
/// The app-layer SqlIdentifier helper lives in the WPF project and is not
/// visible to this project, so the same allow-list approach is implemented
/// here. Values are always bound parameters.
/// </summary>
public sealed class DefinitionRepository : IDefinitionRepository
{
    private const int ListLimit = 1000;
    private static readonly Regex SafePattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private readonly string _provider;
    private readonly string _connectionString;

    public DefinitionRepository(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = NormalizeProvider(provider);
        _connectionString = connectionString;
    }

    internal string Provider => _provider;
    internal string ConnectionString => _connectionString;

    private bool IsPg => _provider == "PostgreSQL";
    private string NotDeleted => IsPg ? "is_deleted = false" : "is_deleted = 0";
    private string DeletedValue => IsPg ? "true" : "1";
    private string ActiveValue(bool active) => IsPg ? (active ? "true" : "false") : (active ? "1" : "0");
    private string NowExpr => IsPg ? "now()" : "datetime('now')";

    // ------------------------------------------------------------ generic sets

    public async Task<IReadOnlyList<DefRow>> ListAsync(DefSet set, string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(set);
        var table = Ident(set, set.Table);
        var idCol = Ident(set, set.IdColumn);
        var select = new List<string> { $"{idCol} AS __id", "company_id AS __company" };
        foreach (var col in set.Columns)
            select.Add($"{Ident(set, col.Name)} AS c_{col.Name}");
        if (set.HasActive)
            select.Add("is_active AS __active");

        var where = new List<string>();
        where.Add(set.IncludeGlobalRows ? "(company_id = @company OR company_id = '')" : "company_id = @company");
        foreach (var fixedCol in set.FixedValues.Keys)
            where.Add($"{Ident(set, fixedCol)} = @fixed_{fixedCol}");
        if (set.HasSoftDelete)
            where.Add(NotDeleted);

        var sql = $"SELECT {string.Join(", ", select)} FROM {table} WHERE {string.Join(" AND ", where)} ORDER BY {Ident(set, set.OrderByColumn)} LIMIT {ListLimit}";
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, sql);
        Add(command, "@company", companyId.Trim());
        foreach (var (k, v) in set.FixedValues)
            Add(command, $"@fixed_{k}", v);
        var result = new List<DefRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new DefRow
            {
                Id = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty,
                CompanyId = ReadString(reader, 1) ?? string.Empty
            };
            var ordinal = 2;
            foreach (var col in set.Columns)
                row.Values[col.Name] = ReadColumnValue(reader, ordinal++, col);
            if (set.HasActive)
                row.IsActive = ReadBool(reader, ordinal);
            result.Add(row);
        }
        return result;
    }

    public async Task<string> SaveAsync(DefSet set, string companyId, string? id, IReadOnlyDictionary<string, object?> values, string actor, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(values);
        var table = Ident(set, set.Table);
        var idCol = Ident(set, set.IdColumn);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Resolve insert vs update. A row owned by another company (a
            // shared global seed row) is never overwritten: saving it creates
            // a company-specific override with a new id.
            var isUpdate = false;
            var effectiveId = id;
            if (!string.IsNullOrWhiteSpace(id))
            {
                await using var owner = CreateCommand(connection, transaction,
                    $"SELECT company_id FROM {table} WHERE {idCol} = @id");
                BindId(owner, "@id", set, id);
                var ownerValue = await owner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (ownerValue is not null && ownerValue is not DBNull)
                {
                    if (string.Equals(Convert.ToString(ownerValue, CultureInfo.InvariantCulture), companyId.Trim(), StringComparison.Ordinal))
                        isUpdate = true;
                    else
                        effectiveId = null; // global seed -> insert an override
                }
            }

            await CheckUniqueAsync(set, connection, transaction, companyId, effectiveId, values, cancellationToken).ConfigureAwait(false);

            string resultId;
            if (!isUpdate)
            {
                resultId = await InsertRowAsync(set, connection, transaction, companyId, effectiveId, values, actor, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await UpdateRowAsync(set, connection, transaction, companyId, effectiveId!, values, actor, cancellationToken).ConfigureAwait(false);
                resultId = effectiveId!;
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return resultId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task CheckUniqueAsync(DefSet set, DbConnection connection, DbTransaction transaction, string companyId, string? excludeId, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken)
    {
        var keys = new List<(string Column, object? Value)>();
        foreach (var (k, v) in set.FixedValues)
            keys.Add((k, v));
        foreach (var key in set.UniqueKeys)
        {
            var column = set.Columns.FirstOrDefault(c => string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Definition set '{set.Key}' unique key '{key}' is not a declared column.");
            keys.Add((key, values.TryGetValue(key, out var v) ? v : null));
        }
        if (keys.Count == 0)
            return;
        var table = Ident(set, set.Table);
        var conditions = new List<string> { "company_id = @company" };
        if (set.HasSoftDelete)
            conditions.Add(NotDeleted);
        if (!string.IsNullOrWhiteSpace(excludeId))
            conditions.Add($"{Ident(set, set.IdColumn)} <> @excludeId");
        await using var command = CreateCommand(connection, transaction,
            $"SELECT COUNT(*) FROM {table} WHERE {string.Join(" AND ", conditions)}");
        Add(command, "@company", companyId.Trim());
        if (!string.IsNullOrWhiteSpace(excludeId))
            BindId(command, "@excludeId", set, excludeId);
        var p = 0;
        foreach (var (columnName, value) in keys)
        {
            var param = $"@uq{p++}";
            var colIdent = Ident(set, columnName);
            // Append the equality condition by rebuilding the command text once.
            command.CommandText += value is null
                ? $" AND {colIdent} IS NULL"
                : $" AND {colIdent} = {param}";
            if (value is not null)
            {
                var defCol = set.Columns.FirstOrDefault(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));
                if (defCol is not null)
                    BindColumnValue(command, param, defCol, value);
                else
                    Add(command, param, value);
            }
        }
        var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (count > 0)
            throw new InvalidOperationException($"A row with the same {string.Join(" + ", keys.Select(k => k.Column))} already exists in set '{set.Key}' for this company.");
    }

    private async Task<string> InsertRowAsync(DefSet set, DbConnection connection, DbTransaction transaction, string companyId, string? requestedId, IReadOnlyDictionary<string, object?> values, string actor, CancellationToken cancellationToken)
    {
        var table = Ident(set, set.Table);
        var columns = new List<string>();
        var parameters = new List<string>();
        var newId = set.IdIsText ? (string.IsNullOrWhiteSpace(requestedId) ? Guid.NewGuid().ToString() : requestedId!) : null;

        void AddColumn(string columnName, string paramName)
        {
            columns.Add(Ident(set, columnName));
            parameters.Add(paramName);
        }

        if (set.IdIsText)
            AddColumn(set.IdColumn, "@id");
        AddColumn("company_id", "@company");
        foreach (var fixedCol in set.FixedValues.Keys)
            AddColumn(fixedCol, $"@fixed_{fixedCol}");
        foreach (var col in set.Columns)
            AddColumn(col.Name, $"@v_{col.Name}");
        if (set.HasActive)
            AddColumn("is_active", "@active");
        if (set.HasSoftDelete)
            AddColumn("is_deleted", "@deleted");
        if (set.HasAuditColumns)
            AddColumn("created_by", "@actor");

        var sql = $"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", parameters)})";
        if (!set.IdIsText && IsPg)
            sql += $" RETURNING {Ident(set, set.IdColumn)}";
        await using var command = CreateCommand(connection, transaction, sql);
        if (set.IdIsText)
            Add(command, "@id", newId!);
        Add(command, "@company", companyId.Trim());
        foreach (var (k, v) in set.FixedValues)
            Add(command, $"@fixed_{k}", v);
        foreach (var col in set.Columns)
            BindColumnValue(command, $"@v_{col.Name}", col, values.TryGetValue(col.Name, out var v) ? v : null);
        if (set.HasActive)
            Add(command, "@active", BoolParam(values.TryGetValue("is_active", out var a) ? ToBool(a, true) : true));
        if (set.HasSoftDelete)
            Add(command, "@deleted", BoolParam(false));
        if (set.HasAuditColumns)
            Add(command, "@actor", actor);

        if (!set.IdIsText && IsPg)
        {
            var generated = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToString(generated, CultureInfo.InvariantCulture) ?? string.Empty;
        }
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (set.IdIsText)
            return newId!;
        await using var lastId = CreateCommand(connection, transaction, "SELECT last_insert_rowid()");
        var sqliteId = await lastId.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToString(sqliteId, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private async Task UpdateRowAsync(DefSet set, DbConnection connection, DbTransaction transaction, string companyId, string id, IReadOnlyDictionary<string, object?> values, string actor, CancellationToken cancellationToken)
    {
        var table = Ident(set, set.Table);
        var sets = new List<string>();
        foreach (var col in set.Columns)
            sets.Add($"{Ident(set, col.Name)} = @v_{col.Name}");
        if (set.HasActive && values.ContainsKey("is_active"))
            sets.Add("is_active = @active");
        if (set.HasAuditColumns)
        {
            sets.Add("updated_by = @actor");
            sets.Add($"updated_at = {NowExpr}");
        }
        var where = $"company_id = @company AND {Ident(set, set.IdColumn)} = @id" + (set.HasSoftDelete ? $" AND {NotDeleted}" : string.Empty);
        await using var command = CreateCommand(connection, transaction,
            $"UPDATE {table} SET {string.Join(", ", sets)} WHERE {where}");
        Add(command, "@company", companyId.Trim());
        BindId(command, "@id", set, id);
        foreach (var col in set.Columns)
            BindColumnValue(command, $"@v_{col.Name}", col, values.TryGetValue(col.Name, out var v) ? v : null);
        if (set.HasActive && values.ContainsKey("is_active"))
            Add(command, "@active", BoolParam(ToBool(values["is_active"], true)));
        if (set.HasAuditColumns)
            Add(command, "@actor", actor);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException($"Row '{id}' in set '{set.Key}' could not be updated (missing or deleted).");
    }

    public async Task SoftDeleteAsync(DefSet set, string companyId, string id, string actor, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(set);
        var table = Ident(set, set.Table);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        string sql;
        if (set.HasSoftDelete)
        {
            var audit = set.HasAuditColumns ? $", updated_by = @actor, updated_at = {NowExpr}" : string.Empty;
            sql = $"UPDATE {table} SET is_deleted = {DeletedValue}{audit} WHERE company_id = @company AND {Ident(set, set.IdColumn)} = @id AND {NotDeleted}";
        }
        else
        {
            // Legacy sets without an is_deleted column (targets, device routes,
            // material attributes) are physically deleted — the only honest
            // delete those schemas support.
            sql = $"DELETE FROM {table} WHERE company_id = @company AND {Ident(set, set.IdColumn)} = @id";
        }
        await using var command = CreateCommand(connection, null, sql);
        Add(command, "@company", companyId.Trim());
        BindId(command, "@id", set, id);
        if (set.HasSoftDelete && set.HasAuditColumns)
            Add(command, "@actor", actor);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException($"Row '{id}' in set '{set.Key}' was not found or is already deleted.");
    }

    public async Task SetActiveAsync(DefSet set, string companyId, string id, bool isActive, string actor, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(set);
        if (!set.HasActive)
            throw new NotSupportedException($"Definition set '{set.Key}' has no active flag.");
        var table = Ident(set, set.Table);
        var audit = set.HasAuditColumns ? $", updated_by = @actor, updated_at = {NowExpr}" : string.Empty;
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"UPDATE {table} SET is_active = {ActiveValue(isActive)}{audit} WHERE company_id = @company AND {Ident(set, set.IdColumn)} = @id" + (set.HasSoftDelete ? $" AND {NotDeleted}" : string.Empty));
        Add(command, "@company", companyId.Trim());
        BindId(command, "@id", set, id);
        if (set.HasAuditColumns)
            Add(command, "@actor", actor);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException($"Row '{id}' in set '{set.Key}' was not found.");
    }

    // ------------------------------------------------------------ installments

    public async Task<string> CreateInstallmentAsync(string companyId, InstallmentPlan plan, IReadOnlyList<InstallmentScheduleRow> schedule, string actor, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(schedule);
        if (string.IsNullOrWhiteSpace(plan.Id))
            plan.Id = Guid.NewGuid().ToString();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var insert = CreateCommand(connection, transaction,
                "INSERT INTO installments (id, company_id, party_code, doc_ref, total_amount, down_payment, installment_count, start_date, status, notes, created_by) VALUES (@id, @company, @party, @ref, @total, @down, @count, @start, @status, @notes, @actor)"))
            {
                Add(insert, "@id", plan.Id);
                Add(insert, "@company", companyId.Trim());
                Add(insert, "@party", plan.PartyCode.Trim());
                Add(insert, "@ref", (object?)NullIfBlank(plan.DocRef) ?? DBNull.Value);
                Add(insert, "@total", plan.TotalAmount);
                Add(insert, "@down", plan.DownPayment);
                Add(insert, "@count", (long)plan.InstallmentCount);
                Add(insert, "@start", FormatDate(plan.StartDate));
                Add(insert, "@status", plan.Status);
                Add(insert, "@notes", (object?)NullIfBlank(plan.Notes) ?? DBNull.Value);
                Add(insert, "@actor", actor);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (var row in schedule)
            {
                if (string.IsNullOrWhiteSpace(row.Id))
                    row.Id = Guid.NewGuid().ToString();
                row.InstallmentId = plan.Id;
                await using var insertRow = CreateCommand(connection, transaction,
                    "INSERT INTO installment_schedule (id, company_id, installment_id, seq, due_date, amount, paid_amount, status, created_by) VALUES (@id, @company, @plan, @seq, @due, @amount, 0, @status, @actor)");
                Add(insertRow, "@id", row.Id);
                Add(insertRow, "@company", companyId.Trim());
                Add(insertRow, "@plan", plan.Id);
                Add(insertRow, "@seq", (long)row.Seq);
                Add(insertRow, "@due", FormatDate(row.DueDate));
                Add(insertRow, "@amount", row.Amount);
                Add(insertRow, "@status", row.Status);
                Add(insertRow, "@actor", actor);
                await insertRow.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return plan.Id;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<InstallmentPlan>> ListInstallmentsAsync(string companyId, string? partyCode, string? status, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var filters = new List<string> { "i.company_id = @company", NotDeleted.Replace("is_deleted", "i.is_deleted") };
        if (!string.IsNullOrWhiteSpace(partyCode))
            filters.Add("i.party_code = @party");
        if (!string.IsNullOrWhiteSpace(status))
            filters.Add("i.status = @status");
        await using var command = CreateCommand(connection, null,
            $@"SELECT i.id, i.party_code, i.doc_ref, i.total_amount, i.down_payment, i.installment_count, i.start_date, i.status, i.notes,
                      COALESCE((SELECT SUM(paid_amount) FROM installment_schedule s WHERE s.installment_id = i.id), 0) AS paid_total
               FROM installments i
               WHERE {string.Join(" AND ", filters)}
               ORDER BY i.start_date, i.party_code LIMIT {ListLimit}");
        Add(command, "@company", companyId.Trim());
        if (!string.IsNullOrWhiteSpace(partyCode))
            Add(command, "@party", partyCode.Trim());
        if (!string.IsNullOrWhiteSpace(status))
            Add(command, "@status", status.Trim());
        var result = new List<InstallmentPlan>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new InstallmentPlan
            {
                Id = ReadString(reader, 0) ?? string.Empty,
                PartyCode = ReadString(reader, 1) ?? string.Empty,
                DocRef = ReadString(reader, 2),
                TotalAmount = ReadDecimal(reader, 3),
                DownPayment = ReadDecimal(reader, 4),
                InstallmentCount = (int)ReadLong(reader, 5),
                StartDate = ReadDate(reader, 6) ?? DateOnly.MinValue,
                Status = ReadString(reader, 7) ?? InstallmentStatuses.Active,
                Notes = ReadString(reader, 8),
                PaidTotal = ReadDecimal(reader, 9)
            });
        }
        return result;
    }

    public async Task<IReadOnlyList<InstallmentScheduleRow>> ListScheduleAsync(string companyId, string installmentId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            "SELECT id, installment_id, seq, due_date, amount, paid_amount, paid_date, status FROM installment_schedule WHERE company_id = @company AND installment_id = @plan ORDER BY seq");
        Add(command, "@company", companyId.Trim());
        Add(command, "@plan", installmentId);
        var result = new List<InstallmentScheduleRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadScheduleRow(reader));
        return result;
    }

    public async Task SoftDeleteInstallmentAsync(string companyId, string installmentId, string actor, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"UPDATE installments SET is_deleted = {DeletedValue}, updated_by = @actor, updated_at = {NowExpr} WHERE company_id = @company AND id = @id AND {NotDeleted}");
        Add(command, "@company", companyId.Trim());
        Add(command, "@id", installmentId);
        Add(command, "@actor", actor);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Installment plan was not found or is already deleted.");
    }

    public async Task<InstallmentScheduleRow> CollectInstallmentAsync(string companyId, string scheduleId, decimal amount, DateOnly paidDate, string actor, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (amount <= 0)
            throw new InvalidOperationException("Collection amount must be positive.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            InstallmentScheduleRow row;
            string partyCode, installmentId;
            string? docRef;
            await using (var load = CreateCommand(connection, transaction,
                $@"SELECT s.id, s.installment_id, s.seq, s.due_date, s.amount, s.paid_amount, s.paid_date, s.status, i.party_code, i.doc_ref
                   FROM installment_schedule s
                   JOIN installments i ON i.id = s.installment_id
                   WHERE s.company_id = @company AND s.id = @id AND i.company_id = @company AND i.{NotDeleted}"))
            {
                Add(load, "@company", companyId.Trim());
                Add(load, "@id", scheduleId);
                await using var reader = await load.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Installment schedule row was not found for this company.");
                row = ReadScheduleRow(reader);
                partyCode = ReadString(reader, 8) ?? string.Empty;
                docRef = ReadString(reader, 9);
                installmentId = row.InstallmentId;
            }

            if (row.Status == ScheduleStatuses.Paid)
                throw new InvalidOperationException("This installment is already fully paid.");
            var newPaid = row.PaidAmount + amount;
            if (newPaid > row.Amount)
                throw new InvalidOperationException($"Collection {amount} exceeds the remaining {row.Remaining} on this installment.");
            var newStatus = newPaid >= row.Amount ? ScheduleStatuses.Paid : ScheduleStatuses.Partial;

            // party_ledger keys parties by integer id (see migration 010 and
            // CommercialDocumentRepository): the installment party_code must
            // be that numeric id, as everywhere else in the codebase.
            if (!long.TryParse(partyCode.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var partyId))
                throw new InvalidOperationException($"Party code '{partyCode}' is not a numeric party id; a party_ledger credit cannot be posted for it.");

            await using (var update = CreateCommand(connection, transaction,
                $"UPDATE installment_schedule SET paid_amount = @paid, paid_date = @date, status = @status, updated_by = @actor, updated_at = {NowExpr} WHERE company_id = @company AND id = @id"))
            {
                Add(update, "@paid", newPaid);
                Add(update, "@date", FormatDate(paidDate));
                Add(update, "@status", newStatus);
                Add(update, "@actor", actor);
                Add(update, "@company", companyId.Trim());
                Add(update, "@id", scheduleId);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Installment schedule row could not be updated.");
            }

            await using (var ledger = CreateCommand(connection, transaction,
                @"INSERT INTO party_ledger (company_id, party_kind, party_id, doc_kind, doc_id, doc_no, entry_date, debit, credit, currency_code, note)
                  VALUES (@company, 'C', @party, 'INS', NULL, @docNo, @date, 0, @credit, 'SAR', @note)"))
            {
                Add(ledger, "@company", companyId.Trim());
                Add(ledger, "@party", partyId);
                Add(ledger, "@docNo", (object?)NullIfBlank(docRef) ?? installmentId);
                Add(ledger, "@date", FormatDate(paidDate));
                Add(ledger, "@credit", amount);
                Add(ledger, "@note", $"Installment collection seq {row.Seq} for plan {installmentId}");
                await ledger.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Close the plan when every schedule row is fully paid.
            await using (var remaining = CreateCommand(connection, transaction,
                "SELECT COUNT(*) FROM installment_schedule WHERE company_id = @company AND installment_id = @plan AND status <> 'Paid'"))
            {
                Add(remaining, "@company", companyId.Trim());
                Add(remaining, "@plan", installmentId);
                if (Convert.ToInt64(await remaining.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 0)
                {
                    await using var close = CreateCommand(connection, transaction,
                        $"UPDATE installments SET status = 'Completed', updated_by = @actor, updated_at = {NowExpr} WHERE company_id = @company AND id = @plan");
                    Add(close, "@actor", actor);
                    Add(close, "@company", companyId.Trim());
                    Add(close, "@plan", installmentId);
                    await close.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            row.PaidAmount = newPaid;
            row.PaidDate = paidDate;
            row.Status = newStatus;
            return row;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    // ----------------------------------------------------------------- loyalty

    public async Task<IReadOnlyList<LoyaltyAccount>> ListLoyaltyAccountsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT id, party_code, points_balance FROM loyalty_accounts WHERE company_id = @company AND {NotDeleted} ORDER BY party_code LIMIT {ListLimit}");
        Add(command, "@company", companyId.Trim());
        var result = new List<LoyaltyAccount>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new LoyaltyAccount
            {
                Id = ReadString(reader, 0) ?? string.Empty,
                PartyCode = ReadString(reader, 1) ?? string.Empty,
                PointsBalance = ReadDecimal(reader, 2)
            });
        }
        return result;
    }

    public async Task<IReadOnlyList<LoyaltyEntry>> ListLoyaltyEntriesAsync(string companyId, string accountId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            "SELECT id, account_id, entry_date, points, doc_ref, notes FROM loyalty_entries WHERE company_id = @company AND account_id = @account ORDER BY entry_date, created_at");
        Add(command, "@company", companyId.Trim());
        Add(command, "@account", accountId);
        var result = new List<LoyaltyEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new LoyaltyEntry
            {
                Id = ReadString(reader, 0) ?? string.Empty,
                AccountId = ReadString(reader, 1) ?? string.Empty,
                EntryDate = ReadDate(reader, 2) ?? DateOnly.MinValue,
                Points = ReadDecimal(reader, 3),
                DocRef = ReadString(reader, 4),
                Notes = ReadString(reader, 5)
            });
        }
        return result;
    }

    public async Task<decimal> AdjustLoyaltyAsync(string companyId, string partyCode, decimal pointsDelta, string? docRef, string? notes, string actor, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (string.IsNullOrWhiteSpace(partyCode))
            throw new InvalidOperationException("Party code is required for a loyalty adjustment.");
        if (pointsDelta == 0)
            throw new InvalidOperationException("Points adjustment cannot be zero.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string accountId;
            decimal balance;
            await using (var load = CreateCommand(connection, transaction,
                $"SELECT id, points_balance FROM loyalty_accounts WHERE company_id = @company AND party_code = @party AND {NotDeleted}"))
            {
                Add(load, "@company", companyId.Trim());
                Add(load, "@party", partyCode.Trim());
                await using var reader = await load.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    accountId = ReadString(reader, 0) ?? string.Empty;
                    balance = ReadDecimal(reader, 1);
                }
                else
                {
                    if (pointsDelta < 0)
                        throw new InvalidOperationException($"No loyalty account exists for party '{partyCode.Trim()}'; points cannot be redeemed.");
                    accountId = Guid.NewGuid().ToString();
                    balance = 0m;
                    await using var insertAccount = CreateCommand(connection, transaction,
                        "INSERT INTO loyalty_accounts (id, company_id, party_code, points_balance, created_by) VALUES (@id, @company, @party, 0, @actor)");
                    Add(insertAccount, "@id", accountId);
                    Add(insertAccount, "@company", companyId.Trim());
                    Add(insertAccount, "@party", partyCode.Trim());
                    Add(insertAccount, "@actor", actor);
                    await insertAccount.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            var newBalance = balance + pointsDelta;
            if (newBalance < 0)
                throw new InvalidOperationException($"Insufficient loyalty points for party '{partyCode.Trim()}': balance {balance}, requested {Math.Abs(pointsDelta)}.");

            await using (var update = CreateCommand(connection, transaction,
                $"UPDATE loyalty_accounts SET points_balance = @balance, updated_by = @actor, updated_at = {NowExpr} WHERE company_id = @company AND id = @id"))
            {
                Add(update, "@balance", newBalance);
                Add(update, "@actor", actor);
                Add(update, "@company", companyId.Trim());
                Add(update, "@id", accountId);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var entry = CreateCommand(connection, transaction,
                "INSERT INTO loyalty_entries (id, company_id, account_id, entry_date, points, doc_ref, notes, created_by) VALUES (@id, @company, @account, @date, @points, @ref, @notes, @actor)"))
            {
                Add(entry, "@id", Guid.NewGuid().ToString());
                Add(entry, "@company", companyId.Trim());
                Add(entry, "@account", accountId);
                Add(entry, "@date", FormatDate(DateOnly.FromDateTime(DateTime.Now)));
                Add(entry, "@points", pointsDelta);
                Add(entry, "@ref", (object?)NullIfBlank(docRef) ?? DBNull.Value);
                Add(entry, "@notes", (object?)NullIfBlank(notes) ?? DBNull.Value);
                Add(entry, "@actor", actor);
                await entry.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return newBalance;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    // ---------------------------------------------------------------- delivery

    public async Task<IReadOnlyList<DeliveryRequest>> ListDeliveryRequestsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT id, doc_no, request_date, party_code, address, area_code, driver_code, status, amount, notes FROM delivery_requests WHERE company_id = @company AND {NotDeleted} ORDER BY doc_no DESC LIMIT {ListLimit}");
        Add(command, "@company", companyId.Trim());
        var result = new List<DeliveryRequest>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadDeliveryRequest(reader));
        return result;
    }

    public async Task<string> SaveDeliveryRequestAsync(string companyId, DeliveryRequest request, string actor, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var isNew = string.IsNullOrWhiteSpace(request.Id);
            if (isNew)
            {
                request.Id = Guid.NewGuid().ToString();
                if (request.DocNo <= 0)
                {
                    await using var next = CreateCommand(connection, transaction,
                        $"SELECT COALESCE(MAX(doc_no), 0) + 1 FROM delivery_requests WHERE company_id = @company AND {NotDeleted}");
                    Add(next, "@company", companyId.Trim());
                    request.DocNo = (int)Convert.ToInt64(await next.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
                }
                else
                {
                    await using var dup = CreateCommand(connection, transaction,
                        $"SELECT COUNT(*) FROM delivery_requests WHERE company_id = @company AND doc_no = @doc AND {NotDeleted}");
                    Add(dup, "@company", companyId.Trim());
                    Add(dup, "@doc", (long)request.DocNo);
                    if (Convert.ToInt64(await dup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
                        throw new InvalidOperationException($"Delivery request number {request.DocNo} already exists for this company.");
                }
                await using var insert = CreateCommand(connection, transaction,
                    @"INSERT INTO delivery_requests (id, company_id, doc_no, request_date, party_code, address, area_code, driver_code, status, amount, notes, created_by)
                      VALUES (@id, @company, @doc, @date, @party, @address, @area, @driver, @status, @amount, @notes, @actor)");
                BindDelivery(insert, companyId, request, actor);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var update = CreateCommand(connection, transaction,
                    $@"UPDATE delivery_requests SET request_date = @date, party_code = @party, address = @address, area_code = @area, driver_code = @driver, amount = @amount, notes = @notes, updated_by = @actor, updated_at = {NowExpr}
                       WHERE company_id = @company AND id = @id AND {NotDeleted}");
                BindDelivery(update, companyId, request, actor);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Delivery request could not be updated (missing or deleted).");
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return request.Id;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task SetDeliveryStatusAsync(string companyId, string requestId, string newStatus, string? driverCode, string actor, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string current;
            await using (var load = CreateCommand(connection, transaction,
                $"SELECT status FROM delivery_requests WHERE company_id = @company AND id = @id AND {NotDeleted}"))
            {
                Add(load, "@company", companyId.Trim());
                Add(load, "@id", requestId);
                var value = await load.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (value is null or DBNull)
                    throw new InvalidOperationException("Delivery request was not found for this company.");
                current = Convert.ToString(value, CultureInfo.InvariantCulture)!;
            }
            if (!DeliveryStatuses.CanTransition(current, newStatus))
                throw new InvalidOperationException($"Delivery request cannot move from '{current}' to '{newStatus}'.");
            if (newStatus == DeliveryStatuses.Assigned && string.IsNullOrWhiteSpace(driverCode))
                throw new InvalidOperationException("A driver is required to assign a delivery request.");

            await using var update = CreateCommand(connection, transaction,
                $"UPDATE delivery_requests SET status = @status, driver_code = COALESCE(@driver, driver_code), updated_by = @actor, updated_at = {NowExpr} WHERE company_id = @company AND id = @id AND {NotDeleted}");
            Add(update, "@status", newStatus);
            Add(update, "@driver", (object?)NullIfBlank(driverCode) ?? DBNull.Value);
            Add(update, "@actor", actor);
            Add(update, "@company", companyId.Trim());
            Add(update, "@id", requestId);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Delivery request could not be updated.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task SoftDeleteDeliveryRequestAsync(string companyId, string requestId, string actor, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"UPDATE delivery_requests SET is_deleted = {DeletedValue}, updated_by = @actor, updated_at = {NowExpr} WHERE company_id = @company AND id = @id AND {NotDeleted}");
        Add(command, "@company", companyId.Trim());
        Add(command, "@id", requestId);
        Add(command, "@actor", actor);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Delivery request was not found or is already deleted.");
    }

    public async Task<IReadOnlyList<DriverSettlement>> ListDriverSettlementsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT id, driver_code, settle_date, collected_amount, fees_amount, net_amount, status FROM driver_settlements WHERE company_id = @company AND {NotDeleted} ORDER BY settle_date DESC, driver_code LIMIT {ListLimit}");
        Add(command, "@company", companyId.Trim());
        var result = new List<DriverSettlement>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new DriverSettlement
            {
                Id = ReadString(reader, 0) ?? string.Empty,
                DriverCode = ReadString(reader, 1) ?? string.Empty,
                SettleDate = ReadDate(reader, 2) ?? DateOnly.MinValue,
                CollectedAmount = ReadDecimal(reader, 3),
                FeesAmount = ReadDecimal(reader, 4),
                NetAmount = ReadDecimal(reader, 5),
                Status = ReadString(reader, 6) ?? "Open"
            });
        }
        return result;
    }

    public async Task<string> CloseDriverCashAsync(string companyId, string driverCode, DateOnly settleDate, decimal collectedAmount, decimal feesAmount, string actor, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var id = Guid.NewGuid().ToString();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            @"INSERT INTO driver_settlements (id, company_id, driver_code, settle_date, collected_amount, fees_amount, net_amount, status, created_by)
              VALUES (@id, @company, @driver, @date, @collected, @fees, @net, 'Closed', @actor)");
        Add(command, "@id", id);
        Add(command, "@company", companyId.Trim());
        Add(command, "@driver", driverCode.Trim());
        Add(command, "@date", FormatDate(settleDate));
        Add(command, "@collected", collectedAmount);
        Add(command, "@fees", feesAmount);
        Add(command, "@net", collectedAmount - feesAmount);
        Add(command, "@actor", actor);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    // ------------------------------------------------------------------ helpers

    private void BindDelivery(DbCommand command, string companyId, DeliveryRequest request, string actor)
    {
        Add(command, "@id", request.Id);
        Add(command, "@company", companyId.Trim());
        Add(command, "@doc", (long)request.DocNo);
        Add(command, "@date", FormatDate(request.RequestDate));
        Add(command, "@party", (object?)NullIfBlank(request.PartyCode) ?? DBNull.Value);
        Add(command, "@address", (object?)NullIfBlank(request.Address) ?? DBNull.Value);
        Add(command, "@area", (object?)NullIfBlank(request.AreaCode) ?? DBNull.Value);
        Add(command, "@driver", (object?)NullIfBlank(request.DriverCode) ?? DBNull.Value);
        Add(command, "@status", request.Status);
        Add(command, "@amount", request.Amount);
        Add(command, "@notes", (object?)NullIfBlank(request.Notes) ?? DBNull.Value);
        Add(command, "@actor", actor);
    }

    private static InstallmentScheduleRow ReadScheduleRow(DbDataReader reader) => new()
    {
        Id = ReadString(reader, 0) ?? string.Empty,
        InstallmentId = ReadString(reader, 1) ?? string.Empty,
        Seq = (int)ReadLong(reader, 2),
        DueDate = ReadDate(reader, 3) ?? DateOnly.MinValue,
        Amount = ReadDecimal(reader, 4),
        PaidAmount = ReadDecimal(reader, 5),
        PaidDate = ReadDate(reader, 6),
        Status = ReadString(reader, 7) ?? ScheduleStatuses.Due
    };

    private static DeliveryRequest ReadDeliveryRequest(DbDataReader reader) => new()
    {
        Id = ReadString(reader, 0) ?? string.Empty,
        DocNo = (int)ReadLong(reader, 1),
        RequestDate = ReadDate(reader, 2) ?? DateOnly.MinValue,
        PartyCode = ReadString(reader, 3),
        Address = ReadString(reader, 4),
        AreaCode = ReadString(reader, 5),
        DriverCode = ReadString(reader, 6),
        Status = ReadString(reader, 7) ?? DeliveryStatuses.Pending,
        Amount = ReadDecimal(reader, 8),
        Notes = ReadString(reader, 9)
    };

    /// <summary>
    /// Allow-list identifier check: the identifier must be declared by the
    /// set descriptor (table, id column, editable/fixed columns, audit and
    /// flag columns) and match the strict identifier shape. Anything else
    /// throws before SQL is built.
    /// </summary>
    private static string Ident(DefSet set, string identifier)
    {
        if (identifier is null || !SafePattern.IsMatch(identifier))
            throw new ArgumentException($"Unsafe SQL identifier '{identifier}'.");
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            set.Table, set.IdColumn, set.OrderByColumn,
            "company_id", "is_active", "is_deleted", "created_by", "created_at", "updated_by", "updated_at"
        };
        foreach (var col in set.Columns)
            allowed.Add(col.Name);
        foreach (var key in set.FixedValues.Keys)
            allowed.Add(key);
        foreach (var key in set.UniqueKeys)
            allowed.Add(key);
        if (!allowed.Contains(identifier))
            throw new ArgumentException($"SQL identifier '{identifier}' is not in the allow-list of definition set '{set.Key}'.");
        return identifier;
    }

    private void BindId(DbCommand command, string name, DefSet set, string id)
    {
        if (set.IdIsText)
            Add(command, name, id);
        else if (long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
            Add(command, name, numeric);
        else
            throw new InvalidOperationException($"Id '{id}' is not valid for set '{set.Key}'.");
    }

    private void BindColumnValue(DbCommand command, string name, DefColumn column, object? value)
    {
        if (value is null)
        {
            // Bool columns are NOT NULL switches: bind false. Columns flagged
            // HasDefault are NOT NULL with a schema default: bind the type zero.
            object nullValue = column.Type switch
            {
                DefColumnType.Bool => BoolParam(false),
                DefColumnType.Number when column.HasDefault => 0m,
                DefColumnType.Integer when column.HasDefault => 0L,
                DefColumnType.Text when column.HasDefault => string.Empty,
                _ => DBNull.Value
            };
            Add(command, name, nullValue);
            return;
        }
        switch (column.Type)
        {
            case DefColumnType.Text:
                Add(command, name, Convert.ToString(value, CultureInfo.InvariantCulture)!.Trim());
                break;
            case DefColumnType.Number:
                Add(command, name, Convert.ToDecimal(value, CultureInfo.InvariantCulture));
                break;
            case DefColumnType.Integer:
                Add(command, name, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case DefColumnType.Bool:
                Add(command, name, BoolParam(ToBool(value, false)));
                break;
            case DefColumnType.Date:
                Add(command, name, NormalizeDate(value));
                break;
            default:
                Add(command, name, value);
                break;
        }
    }

    private object BoolParam(bool value) => IsPg ? value : (value ? 1L : 0L);

    private static bool ToBool(object? value, bool fallback) => value switch
    {
        null => fallback,
        bool b => b,
        long l => l != 0,
        int i => i != 0,
        string s when bool.TryParse(s, out var parsed) => parsed,
        string s => s == "1",
        _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture)
    };

    private static string NormalizeDate(object value) => value switch
    {
        DateOnly d => FormatDate(d),
        DateTime dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        string s when s.Length >= 10 => s[..10],
        string s => s,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)!
    };

    private static object? ReadColumnValue(DbDataReader reader, int ordinal, DefColumn column)
    {
        if (reader.IsDBNull(ordinal))
            return null;
        return column.Type switch
        {
            DefColumnType.Number => ReadDecimal(reader, ordinal),
            DefColumnType.Integer => ReadLong(reader, ordinal),
            DefColumnType.Bool => ReadBool(reader, ordinal),
            DefColumnType.Date => ReadDate(reader, ordinal)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => ReadString(reader, ordinal)
        };
    }

    private static void RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string FormatDate(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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

    private static string? ReadString(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static long ReadLong(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? 0L : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

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
            long l => l != 0,
            int i => i != 0,
            string s => s == "1" || bool.TryParse(s, out var parsed) && parsed,
            _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture)
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
