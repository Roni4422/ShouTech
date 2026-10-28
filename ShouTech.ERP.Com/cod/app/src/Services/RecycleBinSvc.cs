using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App;
using ShouTech.App.Configuration;

namespace ShouTech.App.Services;

/// <summary>
/// Soft-deleted master-data row exposed by the company recycle bin.
/// Table and column names are always resolved from a static whitelist — never from user input.
/// </summary>
public sealed class RecycleBinItem
{
    public required string TableName { get; init; }
    public required string DatasetKey { get; init; }
    public required string PrimaryKeyColumn { get; init; }
    public required string PrimaryKeyValue { get; init; }
    public required string DisplayLabel { get; init; }
    public string? DeletedAt { get; init; }
    public string? DeletedBy { get; init; }
    public required string CompanyCode { get; init; }
}

/// <summary>Outcome of a restore or purge operation for a single bin item.</summary>
public sealed class RecycleBinMutationResult
{
    public required RecycleBinItem Item { get; init; }
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Company-scoped recycle bin for soft-deleted master data (<c>is_deleted = 1</c>).
/// Supports list, restore, and permanent purge. Identifiers are restricted to a fixed catalog.
/// </summary>
public static class RecycleBinSvc
{
    private const string LogCategory = "RecycleBin";
    private const int MaxRowsPerTable = 500;
    private static readonly Regex SafeIdentifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private sealed record BinTableDefinition(
        string TableName,
        string DatasetKey,
        string[] LabelColumnCandidates,
        string[] PrimaryKeyCandidates,
        bool CompanyIdIsCode = false,
        bool DraftOnly = false);

    /// <summary>Master-data tables only. Transactional tables are intentionally excluded.</summary>
    private static readonly BinTableDefinition[] Catalog =
    [
        new("sales_documents", "Sales invoices",
            ["doc_no", "party_name", "doc_id"],
            ["doc_id"], CompanyIdIsCode: true, DraftOnly: true),
        new("purchase_documents", "Purchase invoices",
            ["doc_no", "party_name", "doc_id"],
            ["doc_id"], CompanyIdIsCode: true, DraftOnly: true),
        new("products", "Products",
            ["product_name_ar", "product_name_en", "product_name", "name", "code", "barcode"],
            ["product_id", "id"]),
        new("materials", "Products",
            ["name_ar", "name_en", "name", "code", "barcode"],
            ["material_id", "id"], CompanyIdIsCode: true),
        new("material_groups", "Material groups",
            ["group_name", "group_name_en", "group_code", "card_no"],
            ["group_id"], CompanyIdIsCode: true),
        new("customers", "Customers",
            ["customer_name_ar", "customer_name", "name", "code"],
            ["customer_id", "id"], CompanyIdIsCode: true),
        new("suppliers", "Suppliers",
            ["supplier_name_ar", "supplier_name", "name", "code"],
            ["supplier_id", "id"], CompanyIdIsCode: true),
        new("accounts", "Accounts",
            ["account_name_ar", "account_name", "name", "code", "account_code"],
            ["account_id", "id"], CompanyIdIsCode: true),
        new("chart_of_accounts", "Accounts",
            ["account_name_ar", "account_name", "name", "code"],
            ["account_id", "id"], CompanyIdIsCode: true),
        new("branches", "Branches",
            ["branch_name_ar", "branch_name", "name", "code"],
            ["branch_id", "id"], CompanyIdIsCode: true),
        new("warehouses", "Warehouses",
            ["warehouse_name_ar", "name", "code"],
            ["warehouse_id", "id"], CompanyIdIsCode: true),
        new("brands", "Brands",
            ["brand_name_ar", "name", "code"],
            ["brand_id", "id"], CompanyIdIsCode: true),
        new("product_categories", "ProductClasses",
            ["category_name_ar", "name", "code"],
            ["category_id", "id"], CompanyIdIsCode: true),
        new("units_of_measure", "Units",
            ["unit_name_ar", "name", "uom_code", "code"],
            ["uom_id", "id"], CompanyIdIsCode: true),
        new("employees", "Employees",
            ["employee_name_ar", "name", "code"],
            ["employee_id", "id"], CompanyIdIsCode: true),
    ];

    public static async Task<IReadOnlyList<RecycleBinItem>> ListAsync(
        string companyCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(companyCode))
            return Array.Empty<RecycleBinItem>();

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureDeletionAuditTableAsync(connection, cancellationToken).ConfigureAwait(false);

        var existingTables = await GetTableNamesAsync(connection, cancellationToken).ConfigureAwait(false);
        var companyId = await FindCompanyIdAsync(connection, companyCode, cancellationToken).ConfigureAwait(false);
        var results = new List<RecycleBinItem>();

        var definitions = Catalog
            .Where(definition => existingTables.Contains(definition.TableName))
            .ToDictionary(definition => definition.TableName, StringComparer.OrdinalIgnoreCase);

        // Deliberately do not auto-discover tables: posted accounting and inventory transactions
        // must never become purgeable merely because a schema adds an is_deleted flag.
        // Add new master-data tables to the reviewed catalog above only after checking their relationships.

        foreach (var definition in definitions.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var page = await ListTableAsync(
                    connection, definition, companyCode, companyId, cancellationToken).ConfigureAwait(false);
                results.AddRange(page);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogWarning($"List skipped for table '{definition.TableName}': {ex.Message}");
            }
        }

        return results
            .OrderByDescending(item => item.DeletedAt ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(item => item.DatasetKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.DisplayLabel, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Installs persistent delete-audit infrastructure for the currently configured database.
    /// Safe to call repeatedly during startup.
    /// </summary>
    public static async Task InitializeAuditInfrastructureAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureDeletionAuditTableAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<int> CountAsync(
        string companyCode,
        CancellationToken cancellationToken = default)
    {
        var items = await ListAsync(companyCode, cancellationToken).ConfigureAwait(false);
        return items.Count;
    }

    public static async Task<RecycleBinMutationResult> RestoreAsync(
        RecycleBinItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ValidateCatalogItem(item);

        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureDeletionAuditTableAsync(connection, cancellationToken).ConfigureAwait(false);

            var columns = await GetColumnNamesAsync(connection, item.TableName, cancellationToken)
                .ConfigureAwait(false);
            var softDeleteColumn = FindColumn(columns, "is_deleted");
            if (softDeleteColumn is null)
                return Failed(item, "This table does not support soft-delete restore.");

            var deletedAtColumn = FindColumn(columns, "deleted_at");
            var deletedByColumn = FindColumn(columns, "deleted_by");
            var softDeleteIsBoolean = await IsBooleanColumnAsync(connection, item.TableName, softDeleteColumn, cancellationToken).ConfigureAwait(false);
            var companyColumn = FindColumn(columns, "company_id") ?? FindColumn(columns, "companyid");
            var definition = Catalog.FirstOrDefault(entry => entry.TableName.Equals(item.TableName, StringComparison.OrdinalIgnoreCase))
                ?? new BinTableDefinition(item.TableName, HumanizeTableName(item.TableName), [], [item.PrimaryKeyColumn],
                    CompanyIdIsCode: item.TableName.Equals("sales_documents", StringComparison.OrdinalIgnoreCase)
                        || item.TableName.Equals("purchase_documents", StringComparison.OrdinalIgnoreCase),
                    DraftOnly: item.TableName.Equals("sales_documents", StringComparison.OrdinalIgnoreCase)
                        || item.TableName.Equals("purchase_documents", StringComparison.OrdinalIgnoreCase));
            var statusColumn = definition.DraftOnly ? FindColumn(columns, "status") : null;
            if (definition.DraftOnly && statusColumn is null)
                return Failed(item, "Draft status is unavailable; this document cannot be restored from the bin.");
            var companyValue = definition.CompanyIdIsCode
                ? item.CompanyCode
                : await FindCompanyIdAsync(connection, item.CompanyCode, cancellationToken).ConfigureAwait(false);
            if (companyColumn is null || companyValue is null)
                return Failed(item, "Company scope could not be verified; restore was blocked.");
            var primaryKeyColumn = ResolveExistingColumn(columns, item.PrimaryKeyColumn);

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var falseLiteral = softDeleteIsBoolean ? "FALSE" : "0";
            var trueLiteral = softDeleteIsBoolean ? "TRUE" : "1";

            var assignments = new List<string> { $"{Quote(softDeleteColumn)} = {falseLiteral}" };
            if (deletedAtColumn is not null)
                assignments.Add($"{Quote(deletedAtColumn)} = NULL");
            if (deletedByColumn is not null)
                assignments.Add($"{Quote(deletedByColumn)} = NULL");
            // The immutable deletion_audit_log keeps the actor and timestamp history.
            // Do not force reactivation: preserve the record's pre-deletion active state.

            var predicates = new List<string>
            {
                $"{Quote(primaryKeyColumn)} = @primaryKey",
                $"{Quote(softDeleteColumn)} = {trueLiteral}"
            };
            if (companyColumn is not null && companyValue is not null)
            {
                predicates.Add($"{Quote(companyColumn)} = @companyValue");
                AddParameter(command, "@companyValue", companyValue);
            }
            if (definition.DraftOnly)
            {
                predicates.Add($"{Quote(statusColumn!)} = @draftStatus");
                AddParameter(command, "@draftStatus", "Draft");
            }
            command.CommandText =
                $"UPDATE {Quote(item.TableName)} SET {string.Join(", ", assignments)} " +
                $"WHERE {string.Join(" AND ", predicates)}";

            AddParameter(command, "@primaryKey", CoercePrimaryKey(item.PrimaryKeyValue));

            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected <= 0)
                return Failed(item, "No matching soft-deleted row was found.");

            await WriteAuditEventAsync(connection, transaction, item, "RESTORE",
                AppState.CurrentUser?.Username ?? Environment.UserName,
                "Restore from recycle bin", cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            LogOperation("Restore",
                $"table={item.TableName} pk={item.PrimaryKeyValue} label={item.DisplayLabel} company={item.CompanyCode}");

            return new RecycleBinMutationResult { Item = item, Succeeded = true };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogError($"Restore failed | table={item.TableName} | pk={item.PrimaryKeyValue} | {ex.Message}", ex);
            return Failed(item, ex.Message);
        }
    }

    public static async Task<IReadOnlyList<RecycleBinMutationResult>> RestoreManyAsync(
        IEnumerable<RecycleBinItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var results = new List<RecycleBinMutationResult>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RestoreAsync(item, cancellationToken).ConfigureAwait(false));
        }
        return results;
    }

    public static async Task<RecycleBinMutationResult> PurgeAsync(
        RecycleBinItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ValidateCatalogItem(item);

        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureDeletionAuditTableAsync(connection, cancellationToken).ConfigureAwait(false);

            var columns = await GetColumnNamesAsync(connection, item.TableName, cancellationToken)
                .ConfigureAwait(false);
            if (FindColumn(columns, "is_deleted") is not { } softDeleteColumn)
                return Failed(item, "Permanent deletion is allowed only for a soft-deleted row in the recycle bin.");
            var companyColumn = FindColumn(columns, "company_id") ?? FindColumn(columns, "companyid");
            var softDeleteIsBoolean = await IsBooleanColumnAsync(connection, item.TableName, softDeleteColumn, cancellationToken).ConfigureAwait(false);
            var definition = Catalog.FirstOrDefault(entry => entry.TableName.Equals(item.TableName, StringComparison.OrdinalIgnoreCase))
                ?? new BinTableDefinition(item.TableName, HumanizeTableName(item.TableName), [], [item.PrimaryKeyColumn],
                    CompanyIdIsCode: item.TableName.Equals("sales_documents", StringComparison.OrdinalIgnoreCase)
                        || item.TableName.Equals("purchase_documents", StringComparison.OrdinalIgnoreCase),
                    DraftOnly: item.TableName.Equals("sales_documents", StringComparison.OrdinalIgnoreCase)
                        || item.TableName.Equals("purchase_documents", StringComparison.OrdinalIgnoreCase));
            var statusColumn = definition.DraftOnly ? FindColumn(columns, "status") : null;
            if (definition.DraftOnly && statusColumn is null)
                return Failed(item, "Draft status is unavailable; permanent deletion was blocked.");
            var companyValue = definition.CompanyIdIsCode
                ? item.CompanyCode
                : await FindCompanyIdAsync(connection, item.CompanyCode, cancellationToken).ConfigureAwait(false);
            if (companyColumn is null || companyValue is null)
                return Failed(item, "Company scope could not be verified; permanent deletion was blocked.");
            var primaryKeyColumn = ResolveExistingColumn(columns, item.PrimaryKeyColumn);

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var predicates = new List<string>
            {
                $"{Quote(primaryKeyColumn)} = @primaryKey",
                $"{Quote(softDeleteColumn)} = {(softDeleteIsBoolean ? "TRUE" : "1")}",
                $"{Quote(companyColumn)} = @companyValue"
            };
            AddParameter(command, "@companyValue", companyValue);
            if (definition.DraftOnly)
            {
                predicates.Add($"{Quote(statusColumn!)} = @draftStatus");
                AddParameter(command, "@draftStatus", "Draft");
            }
            command.CommandText =
                $"DELETE FROM {Quote(item.TableName)} WHERE {string.Join(" AND ", predicates)}";

            AddParameter(command, "@primaryKey", CoercePrimaryKey(item.PrimaryKeyValue));

            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected <= 0)
                return Failed(item, "No matching bin row was found to purge.");

            await WriteAuditEventAsync(connection, transaction, item, "PERMANENT_DELETE",
                AppState.CurrentUser?.Username ?? Environment.UserName,
                "Permanent deletion from recycle bin", cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            LogOperation("Purge",
                $"table={item.TableName} pk={item.PrimaryKeyValue} label={item.DisplayLabel} company={item.CompanyCode}");

            return new RecycleBinMutationResult { Item = item, Succeeded = true };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogError($"Purge failed | table={item.TableName} | pk={item.PrimaryKeyValue} | {ex.Message}", ex);
            return Failed(item, ex.Message);
        }
    }

    private static async Task<IReadOnlyList<RecycleBinItem>> ListTableAsync(
        DbConnection connection,
        BinTableDefinition definition,
        string companyCode,
        object? companyId,
        CancellationToken cancellationToken)
    {
        var columns = await GetColumnNamesAsync(connection, definition.TableName, cancellationToken)
            .ConfigureAwait(false);

        var softDeleteColumn = FindColumn(columns, "is_deleted");
        if (softDeleteColumn is null)
            return Array.Empty<RecycleBinItem>();

        // Fail closed: a recycle-bin query must always be scoped to one company.
        var companyColumn = FindColumn(columns, "company_id") ?? FindColumn(columns, "companyid");
        if (companyColumn is null)
            return Array.Empty<RecycleBinItem>();
        var companyValue = definition.CompanyIdIsCode ? companyCode : companyId;
        if (companyValue is null)
            return Array.Empty<RecycleBinItem>();

        var primaryKeyColumn = definition.PrimaryKeyCandidates
            .Select(candidate => FindColumn(columns, candidate))
            .FirstOrDefault(column => column is not null);
        if (primaryKeyColumn is null)
            return Array.Empty<RecycleBinItem>();

        var labelColumn = definition.LabelColumnCandidates
            .Select(candidate => FindColumn(columns, candidate))
            .FirstOrDefault(column => column is not null) ?? primaryKeyColumn;

        var deletedAtColumn = FindColumn(columns, "deleted_at");
        var deletedByColumn = FindColumn(columns, "deleted_by");
        var softDeleteIsBoolean = await IsBooleanColumnAsync(connection, definition.TableName, softDeleteColumn, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        var selectList = new List<string> { Quote(primaryKeyColumn), Quote(labelColumn) };
        if (deletedAtColumn is not null)
            selectList.Add(Quote(deletedAtColumn));
        if (deletedByColumn is not null)
            selectList.Add(Quote(deletedByColumn));

        var predicates = new List<string>
        {
            $"{Quote(softDeleteColumn)} = {(softDeleteIsBoolean ? "TRUE" : "1")}"
        };
        predicates.Add($"{Quote(companyColumn)} = @companyId");
        AddParameter(command, "@companyId", companyValue);
        if (definition.DraftOnly)
        {
            var statusColumn = FindColumn(columns, "status");
            if (statusColumn is null)
                return Array.Empty<RecycleBinItem>();
            predicates.Add($"{Quote(statusColumn)} = @draftStatus");
            AddParameter(command, "@draftStatus", "Draft");
        }

        command.CommandText =
            $"SELECT {string.Join(", ", selectList)} " +
            $"FROM {Quote(definition.TableName)} " +
            $"WHERE {string.Join(" AND ", predicates)} " +
            $"ORDER BY 1 DESC " +
            $"LIMIT {MaxRowsPerTable}";

        var items = new List<RecycleBinItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var primaryKeyValue = ReadString(reader, 0) ?? string.Empty;
            var label = ReadString(reader, 1);
            if (string.IsNullOrWhiteSpace(label))
                label = primaryKeyValue;

            var ordinal = 2;
            string? deletedAt = null;
            string? deletedBy = null;
            if (deletedAtColumn is not null)
            {
                deletedAt = ReadString(reader, ordinal);
                ordinal++;
            }
            if (deletedByColumn is not null)
                deletedBy = ReadString(reader, ordinal);

            items.Add(new RecycleBinItem
            {
                TableName = definition.TableName,
                DatasetKey = definition.DatasetKey,
                PrimaryKeyColumn = primaryKeyColumn,
                PrimaryKeyValue = primaryKeyValue,
                DisplayLabel = label!,
                DeletedAt = deletedAt,
                DeletedBy = deletedBy,
                CompanyCode = companyCode
            });
        }

        return items;
    }

    /// <summary>Durable audit hook for application delete handlers.</summary>
    public static async Task RecordDeletionAsync(
        string companyCode, string tableName, string recordKey, string actor,
        string method, string? reason, string? recordSnapshot = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(companyCode) || !SafeIdentifier.IsMatch(tableName))
            throw new ArgumentException("A company and valid table name are required.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureDeletionAuditTableAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO deletion_audit_log " +
            "(event_id, company_code, table_name, record_key, action_type, actor, occurred_at, method, reason, record_snapshot) " +
            "VALUES (@eventId, @company, @table, @recordKey, 'DELETE', @actor, @occurredAt, @method, @reason, @snapshot)";
        AddParameter(command, "@eventId", Guid.NewGuid().ToString("D"));
        AddParameter(command, "@company", companyCode);
        AddParameter(command, "@table", tableName);
        AddParameter(command, "@recordKey", recordKey);
        AddParameter(command, "@actor", string.IsNullOrWhiteSpace(actor) ? "unknown" : actor);
        AddParameter(command, "@occurredAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        AddParameter(command, "@method", string.IsNullOrWhiteSpace(method) ? "SOFT_DELETE" : method);
        AddParameter(command, "@reason", (object?)reason ?? DBNull.Value);
        AddParameter(command, "@snapshot", (object?)recordSnapshot ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureDeletionAuditTableAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = connection is NpgsqlConnection
            ? "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = 'deletion_audit_log';"
            : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'deletion_audit_log';";
        var exists = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
        if (!exists)
            throw new InvalidOperationException(
                "Deletion-audit schema is missing. Apply the provider migrations under dba/mig.");

        // Trigger installation remains a separate compatibility step; table definitions are
        // owned by DBA migrations, not by this service.
        try
        {
            if (connection is SqliteConnection)
                await EnsureSqliteDeletionTriggersAsync(connection, cancellationToken).ConfigureAwait(false);
            else if (connection is NpgsqlConnection)
                await EnsurePostgresDeletionTriggersAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogWarning($"Database delete-audit trigger installation was incomplete: {ex.Message}");
        }
    }

    private static async Task EnsurePostgresDeletionTriggersAsync(
        DbConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteSqliteAuditDdlAsync(connection, """
            CREATE OR REPLACE FUNCTION public.shoutech_audit_delete_row()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $audit$
            DECLARE
                row_data jsonb;
                company_value text;
                key_value text;
                actor_value text;
                action_value text;
                method_value text;
            BEGIN
                IF TG_OP = 'UPDATE' THEN
                    row_data := to_jsonb(NEW);
                    action_value := 'DELETE';
                    method_value := 'SOFT_DELETE_TRIGGER';
                    actor_value := COALESCE(row_data ->> 'deleted_by', 'database-trigger');
                    INSERT INTO public.deletion_audit_log
                        (event_id, company_code, table_name, record_key, action_type, actor,
                         occurred_at, method, reason, record_snapshot)
                    VALUES
                        (md5(random()::text || clock_timestamp()::text || txid_current()::text),
                         COALESCE(row_data ->> TG_ARGV[1], ''), TG_TABLE_NAME,
                         COALESCE(row_data ->> TG_ARGV[0], ''), action_value, actor_value,
                         clock_timestamp()::text, method_value,
                         'Captured by database trigger; caller did not provide a reason',
                         row_data::text);
                    RETURN NEW;
                ELSE
                    row_data := to_jsonb(OLD);
                    INSERT INTO public.deletion_audit_log
                        (event_id, company_code, table_name, record_key, action_type, actor,
                         occurred_at, method, reason, record_snapshot)
                    VALUES
                        (md5(random()::text || clock_timestamp()::text || txid_current()::text),
                         COALESCE(row_data ->> TG_ARGV[1], ''), TG_TABLE_NAME,
                         COALESCE(row_data ->> TG_ARGV[0], ''), 'DELETE', 'database-trigger',
                         clock_timestamp()::text, 'HARD_DELETE_TRIGGER',
                         'Captured by database trigger; caller did not provide a reason',
                         row_data::text);
                    RETURN OLD;
                END IF;
            END;
            $audit$;
            """, cancellationToken).ConfigureAwait(false);

        var tables = await GetTableNamesAsync(connection, cancellationToken).ConfigureAwait(false);
        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (table.Equals("deletion_audit_log", StringComparison.OrdinalIgnoreCase) ||
                !SafeIdentifier.IsMatch(table))
                continue;

            try
            {
                var columns = await GetColumnNamesAsync(connection, table, cancellationToken).ConfigureAwait(false);
                var companyColumn = FindColumn(columns, "company_id") ?? FindColumn(columns, "companyid");
                var primaryKey = InferPrimaryKey(table, columns);
                if (companyColumn is null || primaryKey is null)
                    continue;

                var safeSuffix = table.Length > 36 ? table[..36] : table;
                if (FindColumn(columns, "is_deleted") is { } softDeleteColumn)
                {
                    var triggerName = "trg_audit_sd_" + safeSuffix;
                    var booleanFlag = await IsBooleanColumnAsync(connection, table, softDeleteColumn, cancellationToken)
                        .ConfigureAwait(false);
                    var deletedCondition = booleanFlag
                        ? $"NEW.{Quote(softDeleteColumn)} IS TRUE"
                        : $"COALESCE(NEW.{Quote(softDeleteColumn)}, 0) <> 0";
                    var sql = $"""
                        DROP TRIGGER IF EXISTS {Quote(triggerName)} ON public.{Quote(table)};
                        CREATE TRIGGER {Quote(triggerName)}
                        AFTER UPDATE OF {Quote(softDeleteColumn)} ON public.{Quote(table)}
                        FOR EACH ROW
                        WHEN (OLD.{Quote(softDeleteColumn)} IS DISTINCT FROM NEW.{Quote(softDeleteColumn)}
                              AND {deletedCondition})
                        EXECUTE FUNCTION public.shoutech_audit_delete_row('{primaryKey}', '{companyColumn}');
                        """;
                    await ExecuteSqliteAuditDdlAsync(connection, sql, cancellationToken).ConfigureAwait(false);
                }

                var hardDeleteName = "trg_audit_hd_" + safeSuffix;
                var hardSql = $"""
                    DROP TRIGGER IF EXISTS {Quote(hardDeleteName)} ON public.{Quote(table)};
                    CREATE TRIGGER {Quote(hardDeleteName)}
                    AFTER DELETE ON public.{Quote(table)}
                    FOR EACH ROW
                    EXECUTE FUNCTION public.shoutech_audit_delete_row('{primaryKey}', '{companyColumn}');
                    """;
                await ExecuteSqliteAuditDdlAsync(connection, hardSql, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogWarning($"PostgreSQL audit trigger skipped for '{table}': {ex.Message}");
            }
        }
    }

    private static async Task EnsureSqliteDeletionTriggersAsync(
        DbConnection connection, CancellationToken cancellationToken)
    {
        var tables = await GetTableNamesAsync(connection, cancellationToken).ConfigureAwait(false);
        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (table.Equals("deletion_audit_log", StringComparison.OrdinalIgnoreCase) ||
                table.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase) ||
                !SafeIdentifier.IsMatch(table))
                continue;

            IReadOnlyList<string> columns;
            try
            {
                columns = await GetColumnNamesAsync(connection, table, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogWarning($"Audit trigger skipped for '{table}': {ex.Message}");
                continue;
            }

            var companyColumn = FindColumn(columns, "company_id") ?? FindColumn(columns, "companyid");
            var primaryKey = InferPrimaryKey(table, columns);
            if (companyColumn is null || primaryKey is null)
                continue;

            var deletedByColumn = FindColumn(columns, "deleted_by");
            var snapshotNew = BuildSqliteSnapshot(columns, "NEW");
            var snapshotOld = BuildSqliteSnapshot(columns, "OLD");
            var companyNew = $"CAST(NEW.{Quote(companyColumn)} AS TEXT)";
            var companyOld = $"CAST(OLD.{Quote(companyColumn)} AS TEXT)";
            var keyNew = $"CAST(NEW.{Quote(primaryKey)} AS TEXT)";
            var keyOld = $"CAST(OLD.{Quote(primaryKey)} AS TEXT)";
            var actorNew = deletedByColumn is null
                ? "'database-trigger'"
                : $"COALESCE(CAST(NEW.{Quote(deletedByColumn)} AS TEXT), 'database-trigger')";
            var tableLiteral = table.Replace("'", "''", StringComparison.Ordinal);

            if (FindColumn(columns, "is_deleted") is { } softDeleteColumn)
            {
                var triggerName = Quote("trg_audit_soft_delete_" + table);
                await ExecuteSqliteAuditDdlAsync(connection, $"""
                    CREATE TRIGGER IF NOT EXISTS {triggerName}
                    AFTER UPDATE OF {Quote(softDeleteColumn)} ON {Quote(table)}
                    WHEN COALESCE(OLD.{Quote(softDeleteColumn)}, 0) = 0
                         AND COALESCE(NEW.{Quote(softDeleteColumn)}, 0) <> 0
                    BEGIN
                        INSERT INTO deletion_audit_log
                            (event_id, company_code, table_name, record_key, action_type, actor,
                             occurred_at, method, reason, record_snapshot)
                        VALUES
                            (lower(hex(randomblob(16))), {companyNew}, '{tableLiteral}', {keyNew},
                             'DELETE', {actorNew}, strftime('%Y-%m-%dT%H:%M:%fZ','now'),
                             'SOFT_DELETE_TRIGGER',
                             'Captured by database trigger; caller did not provide a reason',
                             {snapshotNew});
                    END;
                    """, cancellationToken).ConfigureAwait(false);
            }

            var hardDeleteTrigger = Quote("trg_audit_hard_delete_" + table);
            await ExecuteSqliteAuditDdlAsync(connection, $"""
                CREATE TRIGGER IF NOT EXISTS {hardDeleteTrigger}
                AFTER DELETE ON {Quote(table)}
                BEGIN
                    INSERT INTO deletion_audit_log
                        (event_id, company_code, table_name, record_key, action_type, actor,
                         occurred_at, method, reason, record_snapshot)
                    VALUES
                        (lower(hex(randomblob(16))), {companyOld}, '{tableLiteral}', {keyOld},
                         'DELETE', 'database-trigger', strftime('%Y-%m-%dT%H:%M:%fZ','now'),
                         'HARD_DELETE_TRIGGER',
                         'Captured by database trigger; caller did not provide a reason',
                         {snapshotOld});
                END;
                """, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string BuildSqliteSnapshot(IReadOnlyList<string> columns, string rowAlias)
    {
        // Keep each json_object call below SQLite's function-argument limit even for
        // wide inventory/material tables, then merge chunks into one JSON snapshot.
        var objects = columns.Chunk(20).Select(chunk =>
        {
            var pairs = chunk.SelectMany(column => new[]
            {
                "'" + column.Replace("'", "''", StringComparison.Ordinal) + "'",
                $"{rowAlias}.{Quote(column)}"
            });
            return "json_object(" + string.Join(", ", pairs) + ")";
        }).ToList();

        return objects.Aggregate((left, right) => $"json_patch({left}, {right})");
    }

    private static async Task ExecuteSqliteAuditDdlAsync(
        DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAuditEventAsync(
        DbConnection connection, DbTransaction transaction, RecycleBinItem item, string action, string actor,
        string details, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO deletion_audit_log " +
            "(event_id, company_code, table_name, record_key, action_type, actor, occurred_at, method, reason, record_snapshot) " +
            "VALUES (@eventId, @company, @table, @recordKey, @action, @actor, @occurredAt, @method, @reason, @snapshot)";
        AddParameter(command, "@eventId", Guid.NewGuid().ToString("D"));
        AddParameter(command, "@company", item.CompanyCode);
        AddParameter(command, "@table", item.TableName);
        AddParameter(command, "@recordKey", item.PrimaryKeyValue);
        AddParameter(command, "@action", action);
        AddParameter(command, "@actor", string.IsNullOrWhiteSpace(actor) ? "unknown" : actor);
        AddParameter(command, "@occurredAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        AddParameter(command, "@method", action);
        AddParameter(command, "@reason", details);
        AddParameter(command, "@snapshot", JsonSerializer.Serialize(new
        {
            item.DatasetKey, item.DisplayLabel, item.DeletedAt, item.DeletedBy
        }));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

       private static void ValidateCatalogItem(RecycleBinItem item)
		{
			if (!SafeIdentifier.IsMatch(item.TableName) || !SafeIdentifier.IsMatch(item.PrimaryKeyColumn))
				throw new InvalidOperationException("Invalid identifier in recycle bin item.");

			var definition = Catalog.FirstOrDefault(entry =>
				entry.TableName.Equals(item.TableName, StringComparison.OrdinalIgnoreCase));
			if (definition is null)
				throw new InvalidOperationException("This table is not approved for recycle-bin operations.");

			if (!definition.PrimaryKeyCandidates.Any(candidate =>
					candidate.Equals(item.PrimaryKeyColumn, StringComparison.OrdinalIgnoreCase)))
				throw new InvalidOperationException("The selected primary key is not approved for this recycle-bin table.");
		}

		private static string? InferPrimaryKey(string tableName, IReadOnlyList<string> columns)
		{
			var singular = tableName.EndsWith("ies", StringComparison.OrdinalIgnoreCase)
				? tableName[..^3] + "y"
				: tableName.EndsWith("s", StringComparison.OrdinalIgnoreCase) ? tableName[..^1] : tableName;
			foreach (var candidate in new[] { singular + "_id", tableName.TrimEnd('_') + "_id", "id", "doc_id", "voucher_id" })
			{
				var found = FindColumn(columns, candidate);
				if (found is not null)
					return found;
			}
			return columns.FirstOrDefault(column => column.EndsWith("_id", StringComparison.OrdinalIgnoreCase));
		}

		private static string HumanizeTableName(string tableName)
			=> CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Regex.Replace(tableName, "_+", " ").Trim());

		private static string ResolveExistingColumn(IReadOnlyList<string> columns, string requested)
		{
			var match = FindColumn(columns, requested);
			if (match is null)
				throw new InvalidOperationException($"Column '{requested}' was not found.");
			return match;
		}

		private static string? FindColumn(IReadOnlyList<string> columns, string name)
			=> columns.FirstOrDefault(column => column.Equals(name, StringComparison.OrdinalIgnoreCase));

		private static RecycleBinMutationResult Failed(RecycleBinItem item, string message)
			=> new() { Item = item, Succeeded = false, ErrorMessage = message };

		private static object CoercePrimaryKey(string value)
		{
			if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var int64))
				return int64;
			if (Guid.TryParse(value, out var guid))
				return guid.ToString("D", CultureInfo.InvariantCulture);
			return value;
		}

		private static string? ReadString(DbDataReader reader, int ordinal)
		{
			if (reader.IsDBNull(ordinal))
				return null;
			return Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
		}

		private static void AddParameter(DbCommand command, string name, object value)
		{
			var parameter = command.CreateParameter();
			parameter.ParameterName = name;
			parameter.Value = value ?? DBNull.Value;
			command.Parameters.Add(parameter);
		}

		// ==== التصحيح المطلوب لحل CS8604 ====
		// كانت: private static string Quote(string identifier)
		// أصبحت: private static string Quote(string? identifier)  مع فحص null صريح.
		private static string Quote(string? identifier)
		{
			if (string.IsNullOrWhiteSpace(identifier))
				throw new ArgumentException("Unsafe SQL identifier: null or empty.", nameof(identifier));
			if (!SafeIdentifier.IsMatch(identifier))
				throw new ArgumentException($"Unsafe SQL identifier: '{identifier}'.", nameof(identifier));
			return $"\"{identifier}\"";
		}

		private static DbConnection CreateConnection()
		{
			var connectionString = DatabaseRuntime.GetConnectionString()
				?? throw new InvalidOperationException("Database connection is not configured.");
			if (DatabaseRuntime.IsPostgreSql())
				return new NpgsqlConnection(connectionString);
			return new SqliteConnection(connectionString);
		}

		private static async Task<HashSet<string>> GetTableNamesAsync(
			DbConnection connection,
			CancellationToken cancellationToken)
		{
			var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			await using var command = connection.CreateCommand();
			command.CommandText = DatabaseRuntime.IsPostgreSql()
				? "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'"
				: "SELECT name FROM sqlite_master WHERE type = 'table'";

			await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
				names.Add(reader.GetString(0));
			return names;
		}

		private static async Task<bool> IsBooleanColumnAsync(
			DbConnection connection, string tableName, string columnName, CancellationToken cancellationToken)
		{
			if (!SafeIdentifier.IsMatch(tableName) || !SafeIdentifier.IsMatch(columnName))
				throw new ArgumentException("Unsafe schema identifier.");

			await using var command = connection.CreateCommand();
			if (DatabaseRuntime.IsPostgreSql())
			{
				command.CommandText = """
					SELECT data_type = 'boolean'
					FROM information_schema.columns
					WHERE table_schema = 'public' AND table_name = @tableName AND column_name = @columnName
					LIMIT 1;
					""";
				AddParameter(command, "@tableName", tableName);
				AddParameter(command, "@columnName", columnName);
				var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
				return result is bool isBoolean && isBoolean;
			}

			command.CommandText = $"PRAGMA table_info({Quote(tableName)})";
			await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				if (!string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
					continue;
				var declaredType = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
				return declaredType.Contains("BOOL", StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		private static async Task<IReadOnlyList<string>> GetColumnNamesAsync(
			DbConnection connection,
			string tableName,
			CancellationToken cancellationToken)
		{
			if (!SafeIdentifier.IsMatch(tableName))
				throw new ArgumentException($"Unsafe table name: '{tableName}'.", nameof(tableName));

			var columns = new List<string>();
			await using var command = connection.CreateCommand();
			if (DatabaseRuntime.IsPostgreSql())
			{
				command.CommandText =
					"SELECT column_name FROM information_schema.columns " +
					"WHERE table_schema = 'public' AND table_name = @tableName";
				AddParameter(command, "@tableName", tableName);
			}
			else
			{
				command.CommandText = $"PRAGMA table_info({Quote(tableName)})";
			}

			await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				columns.Add(DatabaseRuntime.IsPostgreSql()
					? reader.GetString(0)
					: reader.GetString(1));
			}

			return columns;
		}

		private static async Task<object?> FindCompanyIdAsync(
			DbConnection connection,
			string companyCode,
			CancellationToken cancellationToken)
		{
			foreach (var sql in new[]
			{
				"SELECT company_id FROM companies WHERE company_code = @code LIMIT 1",
				"SELECT id FROM companies WHERE code = @code LIMIT 1",
			})
			{
				try
				{
					await using var command = connection.CreateCommand();
					command.CommandText = sql;
					AddParameter(command, "@code", companyCode);
					var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
					if (scalar is not null and not DBNull)
						return scalar;
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
				}
			}

			return null;
		}

		private static void LogOperation(string action, string message)
		{
			try { AppServiceHost.Logging.LogOperation(LogCategory, action, message); }
			catch { }
		}

		private static void LogWarning(string message)
		{
			try { AppServiceHost.Logging.LogWarning(LogCategory, message); }
			catch { }
		}

		private static void LogError(string message, Exception exception)
		{
			try { AppServiceHost.Logging.LogError(LogCategory, message, exception); }
			catch { }
		}
}