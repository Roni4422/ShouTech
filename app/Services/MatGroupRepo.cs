#nullable enable

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using Microsoft.Data.Sqlite;
using Npgsql;

using ShouTech.Domain;
namespace ShouTech.App.Services;

public static class MatGroupRepo
{
    public sealed record MatGroupRec(long Id, string Code, string Name, string NameEn, string? ParentCode, string Status);

    public sealed record MatGroupMember(long GroupId, long MaterialId);

    /// <summary>
    /// All material groups of the current company. This is the
    /// method MatDirWin's group loading always expected — it did
    /// not exist, so the directory's Groups mode silently rendered
    /// nothing (the call went through reflection and found no
    /// method). The material directory tree consumes it directly.
    /// </summary>
    public static List<MatGroupRec> LoadAll()
    {
        var company = RequireCompany();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT group_id, group_code, group_name, group_name_en, parent_code, status
            FROM material_groups
            WHERE company_id = @company
              AND COALESCE(is_deleted, 0) = 0
            ORDER BY group_code;
            """;
        Add(command, "@company", company);

        var result = new List<MatGroupRec>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new MatGroupRec(
                Convert.ToInt64(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetValue(1)?.ToString() ?? "",
                reader.GetValue(2)?.ToString() ?? "",
                reader.GetValue(3)?.ToString() ?? "",
                reader.IsDBNull(4) ? null : reader.GetValue(4)?.ToString(),
                reader.GetValue(5)?.ToString() ?? "Active"));
        }
        return result;
    }

    /// <summary>Every (group, material) membership pair of the
    /// current company — the tree's child linkage.</summary>
    public static List<MatGroupMember> LoadMembership()
    {
        var company = RequireCompany();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT group_id, material_id
            FROM material_group_items
            WHERE company_id = @company
            ORDER BY group_id, material_id;
            """;
        Add(command, "@company", company);

        var result = new List<MatGroupMember>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new MatGroupMember(
                Convert.ToInt64(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture)));
        }
        return result;
    }

    public static HashSet<long> LoadMemberIds(long groupId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(groupId);

        var company = RequireCompany();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT material_id
            FROM material_group_items
            WHERE company_id = @company
              AND group_id = @groupId
            ORDER BY material_id;
            """;
        Add(command, "@company", company);
        Add(command, "@groupId", groupId);

        var result = new HashSet<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(Convert.ToInt64(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture));

        return result;
    }

    public static void SaveMembers(long groupId, IEnumerable<long> materialIds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(groupId);

        ArgumentNullException.ThrowIfNull(materialIds);

        var company = RequireCompany();
        var ids = materialIds.Where(id => id > 0).Distinct().OrderBy(id => id).ToArray();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        try
        {
            using (var groupCheck = connection.CreateCommand())
            {
                groupCheck.Transaction = transaction;
                groupCheck.CommandText = """
                    SELECT COUNT(*)
                    FROM material_groups
                    WHERE company_id = @company
                      AND group_id = @groupId
                      AND COALESCE(is_deleted, 0) = 0;
                    """;
                Add(groupCheck, "@company", company);
                Add(groupCheck, "@groupId", groupId);
                if (Convert.ToInt32(groupCheck.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 1)
                    throw new InvalidOperationException("The selected material group is no longer available.");
            }

            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = """
                    DELETE FROM material_group_items
                    WHERE company_id = @company
                      AND group_id = @groupId;
                    """;
                Add(delete, "@company", company);
                Add(delete, "@groupId", groupId);
                delete.ExecuteNonQuery();
            }

            foreach (var materialId in ids)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                var activeMaterialPredicate = DatabaseRuntime.IsPostgreSql()
                    ? "is_deleted = FALSE"
                    : "COALESCE(is_deleted, 0) = 0";
                insert.CommandText = $"""
                    INSERT INTO material_group_items
                        (company_id, group_id, material_id, created_at, created_by)
                    SELECT @company, @groupId, material_id, @createdAt, @createdBy
                    FROM materials
                    WHERE company_id = @company
                      AND material_id = @materialId
                      AND {activeMaterialPredicate};
                    """;
                Add(insert, "@company", company);
                Add(insert, "@groupId", groupId);
                Add(insert, "@materialId", materialId);
                Add(insert, "@createdAt", DatabaseRuntime.IsPostgreSql()
                    ? DateTimeOffset.UtcNow
                    : DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                Add(insert, "@createdBy", AppState.CurrentUser?.Username ?? string.Empty);

                if (insert.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException($"Material {materialId} is not available in the selected company.");
            }

            transaction.Commit();
        }
        catch
        {
            try { transaction.Rollback(); } catch (Exception exL114) {
                AppHost.Errors.Report(ErrorCodes.InventoryOperationFailed, exL114, "MatGroupRepo.SaveMembers"); }
            throw;
        }
    }

    private static string RequireCompany()
    {
        var company = AppState.CurrentCompanyCode?.Trim();
        if (string.IsNullOrWhiteSpace(company))
            throw new InvalidOperationException("Select a company before managing material groups.");
        return company;
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
