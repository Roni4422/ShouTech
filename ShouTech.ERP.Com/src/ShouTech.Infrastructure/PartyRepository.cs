using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Infrastructure;

public sealed class PartyRepository : IPartyRepository
{
    private readonly string _provider;
    private readonly string _connectionString;

    public PartyRepository(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = provider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase) ||
                    provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase)
            ? "PostgreSQL"
            : provider.Equals("SQLite", StringComparison.OrdinalIgnoreCase)
                ? "SQLite"
                : throw new ArgumentException("Only SQLite and PostgreSQL are supported.", nameof(provider));
        _connectionString = connectionString;
    }

    public async Task<IReadOnlyList<Party>> GetAsync(
        Guid companyId,
        string kind,
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT party_id, company_id, party_code, party_kind, name_en, name_ar, group_code,
                   tax_number, commercial_registration, credit_limit, credit_days, price_tier,
                   gl_account_code, phone, mobile, email, address, is_active
            FROM parties
            WHERE company_id = @company AND party_kind = @kind AND is_deleted = @deleted
              AND (@includeInactive = @includeInactiveTrue OR is_active = @active)
            ORDER BY party_code
            """;
        Add(command, "@company", _provider == "PostgreSQL" ? companyId : companyId.ToString("D"));
        Add(command, "@kind", kind);
        Add(command, "@deleted", _provider == "PostgreSQL" ? false : 0);
        Add(command, "@includeInactive", true);
        Add(command, "@includeInactiveTrue", includeInactive);
        Add(command, "@active", _provider == "PostgreSQL" ? true : 1);

        var parties = new List<Party>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var storedCompany = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture);
            if (!Guid.TryParse(storedCompany, out var parsedCompany) || parsedCompany != companyId)
                throw new InvalidOperationException("A party belongs to an unexpected company.");
            parties.Add(new Party(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                parsedCompany,
                reader.GetString(2),
                reader.GetString(3).Trim(),
                reader.GetString(4),
                NullableString(reader, 5),
                NullableString(reader, 6),
                NullableString(reader, 7),
                NullableString(reader, 8),
                Convert.ToDecimal(reader.GetValue(9), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(10), CultureInfo.InvariantCulture),
                NullableString(reader, 11),
                NullableString(reader, 12),
                NullableString(reader, 13),
                NullableString(reader, 14),
                NullableString(reader, 15),
                NullableString(reader, 16),
                Convert.ToBoolean(reader.GetValue(17), CultureInfo.InvariantCulture)));
        }
        return parties.AsReadOnly();
    }

    public async Task SaveAsync(PartyDraft party, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(party);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO parties (
                company_id, party_code, party_kind, name_en, name_ar, group_code, tax_number,
                commercial_registration, credit_limit, credit_days, price_tier, gl_account_code,
                phone, mobile, email, address, is_active, created_by, updated_by, updated_at)
            VALUES (
                @company, @code, @kind, @nameEn, @nameAr, @group, @tax, @registration,
                @creditLimit, @creditDays, @priceTier, @glAccount, @phone, @mobile, @email,
                @address, @active, @actor, @actor, CURRENT_TIMESTAMP)
            ON CONFLICT(company_id, party_kind, party_code) DO UPDATE SET
                name_en = excluded.name_en,
                name_ar = excluded.name_ar,
                group_code = excluded.group_code,
                tax_number = excluded.tax_number,
                commercial_registration = excluded.commercial_registration,
                credit_limit = excluded.credit_limit,
                credit_days = excluded.credit_days,
                price_tier = excluded.price_tier,
                gl_account_code = excluded.gl_account_code,
                phone = excluded.phone,
                mobile = excluded.mobile,
                email = excluded.email,
                address = excluded.address,
                is_active = excluded.is_active,
                updated_by = excluded.updated_by,
                updated_at = CURRENT_TIMESTAMP
            """;
        Add(command, "@company", _provider == "PostgreSQL" ? party.CompanyId : party.CompanyId.ToString("D"));
        Add(command, "@code", party.Code);
        Add(command, "@kind", party.Kind);
        Add(command, "@nameEn", party.NameEnglish);
        Add(command, "@nameAr", (object?)party.NameArabic ?? DBNull.Value);
        Add(command, "@group", (object?)party.GroupCode ?? DBNull.Value);
        Add(command, "@tax", (object?)party.TaxNumber ?? DBNull.Value);
        Add(command, "@registration", (object?)party.CommercialRegistration ?? DBNull.Value);
        Add(command, "@creditLimit", party.CreditLimit);
        Add(command, "@creditDays", party.CreditDays);
        Add(command, "@priceTier", (object?)party.PriceTier ?? DBNull.Value);
        Add(command, "@glAccount", (object?)party.GlAccountCode ?? DBNull.Value);
        Add(command, "@phone", (object?)party.Phone ?? DBNull.Value);
        Add(command, "@mobile", (object?)party.Mobile ?? DBNull.Value);
        Add(command, "@email", (object?)party.Email ?? DBNull.Value);
        Add(command, "@address", (object?)party.Address ?? DBNull.Value);
        Add(command, "@active", _provider == "PostgreSQL" ? party.IsActive : party.IsActive ? 1 : 0);
        Add(command, "@actor", (object?)party.Actor ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private DbConnection CreateConnection()
        => _provider == "PostgreSQL" ? new NpgsqlConnection(_connectionString) : new SqliteConnection(_connectionString);

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string? NullableString(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
}
