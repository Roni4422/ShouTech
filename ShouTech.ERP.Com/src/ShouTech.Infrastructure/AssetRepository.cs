using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Infrastructure;

/// <summary>
/// Provider-neutral Fixed Assets persistence (SQLite + PostgreSQL), following
/// the JournalRepository patterns: parameterized SQL only, company scoping,
/// soft deletes, single-transaction writes with rollback. Depreciation is
/// computed by the application service and never stored.
/// </summary>
public sealed class AssetRepository : IAssetRepository
{
    private const int ListLimit = 500;

    private readonly string _provider;
    private readonly string _connectionString;

    public AssetRepository(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = NormalizeProvider(provider);
        _connectionString = connectionString;
    }

    private string NotDeleted => _provider == "PostgreSQL" ? "is_deleted = false" : "is_deleted = 0";
    private string DeletedValue => _provider == "PostgreSQL" ? "true" : "1";
    private string NowExpr => _provider == "PostgreSQL" ? "now()" : "datetime('now')";

    public async Task<IReadOnlyList<FixedAsset>> GetAllAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT asset_id, asset_code, asset_name, category, purchase_date, cost, salvage_value, useful_life_years, depreciation_method, location, status, notes FROM fixed_assets WHERE company_id = @company AND {NotDeleted} ORDER BY asset_code LIMIT {ListLimit}");
        Add(command, "@company", companyId.Trim());
        var result = new List<FixedAsset>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new FixedAsset
            {
                Id = ReadGuid(reader, 0),
                Code = ReadString(reader, 1) ?? string.Empty,
                Name = ReadString(reader, 2) ?? string.Empty,
                Category = ReadString(reader, 3),
                PurchaseDate = ReadDate(reader, 4) ?? DateOnly.FromDateTime(DateTime.Today),
                Cost = ReadDecimal(reader, 5),
                SalvageValue = ReadDecimal(reader, 6),
                UsefulLifeYears = Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture),
                DepreciationMethod = ReadString(reader, 8) ?? DepreciationMethods.StraightLine,
                Location = ReadString(reader, 9),
                Status = ReadString(reader, 10) ?? AssetStatuses.Active,
                Notes = ReadString(reader, 11)
            });
        }
        return result;
    }

    public async Task SaveAsync(string companyId, FixedAsset asset, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(asset);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var dup = CreateCommand(connection, transaction,
                $"SELECT COUNT(*) FROM fixed_assets WHERE company_id = @company AND asset_code = @code AND {NotDeleted} AND asset_id <> @id"))
            {
                Add(dup, "@company", companyId.Trim());
                Add(dup, "@code", asset.Code.Trim());
                Add(dup, "@id", FormatGuid(asset.Id));
                if (Convert.ToInt64(await dup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
                    throw new InvalidOperationException($"Asset code '{asset.Code.Trim()}' already exists for this company.");
            }

            long exists;
            await using (var check = CreateCommand(connection, transaction,
                "SELECT COUNT(*) FROM fixed_assets WHERE company_id = @company AND asset_id = @id"))
            {
                Add(check, "@company", companyId.Trim());
                Add(check, "@id", FormatGuid(asset.Id));
                exists = Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }

            if (exists == 0)
            {
                await using var insert = CreateCommand(connection, transaction,
                    "INSERT INTO fixed_assets (asset_id, company_id, asset_code, asset_name, category, purchase_date, cost, salvage_value, useful_life_years, depreciation_method, location, status, notes, created_by) VALUES (@id, @company, @code, @name, @category, @purchase, @cost, @salvage, @life, @method, @location, @status, @notes, @user)");
                Bind(insert, companyId, asset, userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var update = CreateCommand(connection, transaction,
                    $"UPDATE fixed_assets SET asset_code = @code, asset_name = @name, category = @category, purchase_date = @purchase, cost = @cost, salvage_value = @salvage, useful_life_years = @life, depreciation_method = @method, location = @location, status = @status, notes = @notes, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND asset_id = @id AND {NotDeleted}");
                Bind(update, companyId, asset, userId);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Asset could not be updated (missing or deleted).");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task SoftDeleteAsync(string companyId, Guid assetId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"UPDATE fixed_assets SET is_deleted = {DeletedValue}, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND asset_id = @id AND {NotDeleted}");
        Add(command, "@company", companyId.Trim());
        Add(command, "@id", FormatGuid(assetId));
        Add(command, "@user", userId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Asset was not found or is already deleted.");
    }

    private void Bind(DbCommand command, string companyId, FixedAsset asset, string userId)
    {
        Add(command, "@id", FormatGuid(asset.Id));
        Add(command, "@company", companyId.Trim());
        Add(command, "@code", asset.Code.Trim());
        Add(command, "@name", asset.Name.Trim());
        Add(command, "@category", (object?)NullIfBlank(asset.Category) ?? DBNull.Value);
        Add(command, "@purchase", asset.PurchaseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(command, "@cost", asset.Cost);
        Add(command, "@salvage", asset.SalvageValue);
        Add(command, "@life", asset.UsefulLifeYears);
        Add(command, "@method", asset.DepreciationMethod);
        Add(command, "@location", (object?)NullIfBlank(asset.Location) ?? DBNull.Value);
        Add(command, "@status", asset.Status);
        Add(command, "@notes", (object?)NullIfBlank(asset.Notes) ?? DBNull.Value);
        Add(command, "@user", userId);
    }

    private static void RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
