#nullable enable

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace ShouTech.App.Services;

public sealed class ProductCategoryRecord
{
    public string Id { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string? ParentId { get; set; }
    public string? ParentCode { get; set; }
    public int Level { get; set; } = 1;
    public bool IsActive { get; set; } = true;
}

public static class ProductCategoryRepo
{
    public static List<ProductCategoryRecord> LoadAll()
    {
        var companyCode = RequireCompanyCode();
        using var connection = OpenConnection();
        var companyId = ResolveCompanyId(connection, companyCode);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                category.category_id,
                category.category_code,
                category.category_name_ar,
                category.category_name_en,
                category.parent_category_id,
                parent.category_code AS parent_code,
                category.category_level,
                category.is_active
            FROM product_categories category
            LEFT JOIN product_categories parent
                ON parent.category_id = category.parent_category_id
               AND parent.company_id = category.company_id
               AND parent.is_deleted = 0
            WHERE category.company_id = @companyId
              AND category.is_deleted = 0
            ORDER BY category.category_level, category.category_code;
            """;
        Add(command, "@companyId", companyId);

        var records = new List<ProductCategoryRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(new ProductCategoryRecord
            {
                Id = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty,
                Code = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty,
                NameAr = reader.IsDBNull(2) ? string.Empty : Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture) ?? string.Empty,
                NameEn = reader.IsDBNull(3) ? string.Empty : Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture) ?? string.Empty,
                ParentId = reader.IsDBNull(4) ? null : Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture),
                ParentCode = reader.IsDBNull(5) ? null : Convert.ToString(reader.GetValue(5), CultureInfo.InvariantCulture),
                Level = reader.IsDBNull(6) ? 1 : Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture),
                IsActive = !reader.IsDBNull(7) && Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture) != 0
            });
        }

        return records;
    }

    public static ProductCategoryRecord Save(ProductCategoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var code = record.Code.Trim();
        var nameAr = record.NameAr.Trim();
        var nameEn = record.NameEn.Trim();
        var parentId = string.IsNullOrWhiteSpace(record.ParentId) ? null : record.ParentId.Trim();
        var existingId = string.IsNullOrWhiteSpace(record.Id) ? null : record.Id.Trim();

        if (code.Length == 0)
            throw new ArgumentException("A category code is required.", nameof(record));
        if (code.Length > 64)
            throw new ArgumentException("The category code cannot exceed 64 characters.", nameof(record));
        if (nameAr.Length == 0 && nameEn.Length == 0)
            throw new ArgumentException("Enter an Arabic or English category name.", nameof(record));

        var companyCode = RequireCompanyCode();
        using var connection = OpenConnection();
        var companyId = ResolveCompanyId(connection, companyCode);
        using var transaction = connection.BeginTransaction();

        try
        {
            var id = existingId ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            if (string.Equals(parentId, id, StringComparison.Ordinal))
                throw new InvalidOperationException("A category cannot be its own parent.");

            var level = 1;
            if (parentId is not null)
            {
                level = GetParentLevel(connection, transaction, companyId, parentId) + 1;
                EnsureNoParentCycle(connection, transaction, companyId, id, parentId);
            }

            using (var duplicateCheck = connection.CreateCommand())
            {
                duplicateCheck.Transaction = transaction;
                duplicateCheck.CommandText = """
                    SELECT COUNT(*)
                    FROM product_categories
                    WHERE company_id = @companyId
                      AND LOWER(category_code) = LOWER(@code)
                      AND is_deleted = 0
                      AND (@categoryId IS NULL OR category_id <> @categoryId);
                    """;
                Add(duplicateCheck, "@companyId", companyId);
                Add(duplicateCheck, "@code", code);
                Add(duplicateCheck, "@categoryId", existingId);
                if (Convert.ToInt32(duplicateCheck.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
                    throw new InvalidOperationException("The category code is already used in this company.");
            }

            if (existingId is null)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO product_categories
                        (category_id, company_id, parent_category_id, category_code,
                         category_name_ar, category_name_en, category_level, is_active,
                         is_deleted, created_at, created_by)
                    VALUES
                        (@categoryId, @companyId, @parentId, @code,
                         @nameAr, @nameEn, @level, @isActive,
                         0, CURRENT_TIMESTAMP, @userId);
                    """;
                Add(insert, "@categoryId", id);
                Add(insert, "@companyId", companyId);
                Add(insert, "@parentId", parentId);
                Add(insert, "@code", code);
                Add(insert, "@nameAr", nameAr);
                Add(insert, "@nameEn", nameEn);
                Add(insert, "@level", level);
                Add(insert, "@isActive", record.IsActive ? 1 : 0);
                Add(insert, "@userId", AppState.CurrentUser?.Username ?? string.Empty);
                insert.ExecuteNonQuery();
            }
            else
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE product_categories
                    SET parent_category_id = @parentId,
                        category_code = @code,
                        category_name_ar = @nameAr,
                        category_name_en = @nameEn,
                        category_level = @level,
                        is_active = @isActive,
                        modified_at = CURRENT_TIMESTAMP,
                        modified_by = @userId
                    WHERE category_id = @categoryId
                      AND company_id = @companyId
                      AND is_deleted = 0;
                    """;
                Add(update, "@parentId", parentId);
                Add(update, "@code", code);
                Add(update, "@nameAr", nameAr);
                Add(update, "@nameEn", nameEn);
                Add(update, "@level", level);
                Add(update, "@isActive", record.IsActive ? 1 : 0);
                Add(update, "@userId", AppState.CurrentUser?.Username ?? string.Empty);
                Add(update, "@categoryId", id);
                Add(update, "@companyId", companyId);

                if (update.ExecuteNonQuery() != 1)
                    throw new KeyNotFoundException("The selected product category no longer exists.");

                RecalculateDescendantLevels(connection, transaction, companyId, id, level);
            }

            transaction.Commit();
            return new ProductCategoryRecord
            {
                Id = id,
                Code = code,
                NameAr = nameAr,
                NameEn = nameEn,
                ParentId = parentId,
                Level = level,
                IsActive = record.IsActive
            };
        }
        catch
        {
            try { transaction.Rollback(); } catch { }
            throw;
        }
    }

    public static void SoftDelete(string categoryId)
    {
        if (string.IsNullOrWhiteSpace(categoryId))
            throw new ArgumentException("A category must be selected.", nameof(categoryId));

        var companyCode = RequireCompanyCode();
        using var connection = OpenConnection();
        var companyId = ResolveCompanyId(connection, companyCode);
        using var transaction = connection.BeginTransaction();

        try
        {
            string code;
            using (var category = connection.CreateCommand())
            {
                category.Transaction = transaction;
                category.CommandText = """
                    SELECT category_code
                    FROM product_categories
                    WHERE category_id = @categoryId
                      AND company_id = @companyId
                      AND is_deleted = 0;
                    """;
                Add(category, "@categoryId", categoryId);
                Add(category, "@companyId", companyId);
                code = Convert.ToString(category.ExecuteScalar(), CultureInfo.InvariantCulture)
                    ?? throw new KeyNotFoundException("The selected product category no longer exists.");
            }

            using (var childCheck = connection.CreateCommand())
            {
                childCheck.Transaction = transaction;
                childCheck.CommandText = """
                    SELECT COUNT(*)
                    FROM product_categories
                    WHERE company_id = @companyId
                      AND parent_category_id = @categoryId
                      AND is_deleted = 0;
                    """;
                Add(childCheck, "@companyId", companyId);
                Add(childCheck, "@categoryId", categoryId);
                if (Convert.ToInt32(childCheck.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
                    throw new InvalidOperationException("Move or delete the child categories before deleting this category.");
            }

            var activeMaterialPredicate = DatabaseRuntime.IsPostgreSql()
                ? "is_deleted = FALSE"
                : "COALESCE(is_deleted, 0) = 0";
            using (var materialCheck = connection.CreateCommand())
            {
                materialCheck.Transaction = transaction;
                materialCheck.CommandText = $"""
                    SELECT COUNT(*)
                    FROM materials
                    WHERE company_id = @companyCode
                      AND (category = @categoryId OR category = @categoryCode)
                      AND {activeMaterialPredicate};
                    """;
                Add(materialCheck, "@companyCode", companyCode);
                Add(materialCheck, "@categoryId", categoryId);
                Add(materialCheck, "@categoryCode", code);
                if (Convert.ToInt32(materialCheck.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
                    throw new InvalidOperationException("Reassign the materials using this category before deleting it.");
            }

            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = """
                    UPDATE product_categories
                    SET is_deleted = 1,
                        is_active = 0,
                        deleted_at = CURRENT_TIMESTAMP,
                        deleted_by = @userId
                    WHERE category_id = @categoryId
                      AND company_id = @companyId
                      AND is_deleted = 0;
                    """;
                Add(delete, "@userId", AppState.CurrentUser?.Username ?? string.Empty);
                Add(delete, "@categoryId", categoryId);
                Add(delete, "@companyId", companyId);
                if (delete.ExecuteNonQuery() != 1)
                    throw new KeyNotFoundException("The selected product category no longer exists.");
            }

            transaction.Commit();
        }
        catch
        {
            try { transaction.Rollback(); } catch { }
            throw;
        }
    }

    private static int GetParentLevel(DbConnection connection, DbTransaction transaction, object companyId, string parentId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT category_level
            FROM product_categories
            WHERE category_id = @parentId
              AND company_id = @companyId
              AND is_deleted = 0;
            """;
        Add(command, "@parentId", parentId);
        Add(command, "@companyId", companyId);
        var value = command.ExecuteScalar();
        if (value is null or DBNull)
            throw new InvalidOperationException("Select an active parent category from this company.");
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static void EnsureNoParentCycle(
        DbConnection connection,
        DbTransaction transaction,
        object companyId,
        string categoryId,
        string parentId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = parentId;

        while (!string.IsNullOrWhiteSpace(cursor))
        {
            if (string.Equals(cursor, categoryId, StringComparison.Ordinal))
                throw new InvalidOperationException("A category cannot be moved below one of its own descendants.");
            if (!visited.Add(cursor))
                throw new InvalidOperationException("The existing category hierarchy contains a cycle.");

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT parent_category_id
                FROM product_categories
                WHERE category_id = @categoryId
                  AND company_id = @companyId
                  AND is_deleted = 0;
                """;
            Add(command, "@categoryId", cursor);
            Add(command, "@companyId", companyId);
            var value = command.ExecuteScalar();
            cursor = value is null or DBNull
                ? null
                : Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }

    private static void RecalculateDescendantLevels(
        DbConnection connection,
        DbTransaction transaction,
        object companyId,
        string categoryId,
        int categoryLevel)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH RECURSIVE descendants(category_id, category_level) AS
            (
                SELECT category_id, @categoryLevel + 1
                FROM product_categories
                WHERE company_id = @companyId
                  AND parent_category_id = @categoryId
                  AND is_deleted = 0
                UNION ALL
                SELECT child.category_id, parent.category_level + 1
                FROM product_categories child
                INNER JOIN descendants parent
                    ON child.parent_category_id = parent.category_id
                WHERE child.company_id = @companyId
                  AND child.is_deleted = 0
            )
            UPDATE product_categories
            SET category_level = (
                SELECT descendants.category_level
                FROM descendants
                WHERE descendants.category_id = product_categories.category_id
            )
            WHERE company_id = @companyId
              AND category_id IN (SELECT category_id FROM descendants);
            """;
        Add(command, "@categoryLevel", categoryLevel);
        Add(command, "@companyId", companyId);
        Add(command, "@categoryId", categoryId);
        command.ExecuteNonQuery();
    }

    private static object ResolveCompanyId(DbConnection connection, string companyCode)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT company_id
            FROM companies
            WHERE company_code = @companyCode
            LIMIT 1;
            """;
        Add(command, "@companyCode", companyCode);
        var value = command.ExecuteScalar();
        if (value is null or DBNull)
            throw new InvalidOperationException("The selected company could not be resolved in the company registry.");

        if (DatabaseRuntime.IsPostgreSql())
        {
            if (value is Guid id)
                return id;
            if (Guid.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out id))
                return id;
            throw new InvalidOperationException("The selected company has an invalid database identity.");
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException("The selected company has an invalid database identity.");
    }

    private static string RequireCompanyCode()
    {
        var companyCode = AppState.CurrentCompanyCode?.Trim();
        if (string.IsNullOrWhiteSpace(companyCode))
            throw new InvalidOperationException("Select a company before managing product categories.");
        return companyCode;
    }

    private static DbConnection OpenConnection()
    {
        var connectionString = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("The active company database connection is not configured.");

        DbConnection connection = DatabaseRuntime.IsPostgreSql()
            ? new NpgsqlConnection(connectionString)
            : new SqliteConnection(DatabaseRuntime.NormalizeSqliteConnectionString(connectionString));
        connection.Open();
        return connection;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
