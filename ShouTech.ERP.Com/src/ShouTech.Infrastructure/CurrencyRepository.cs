using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Infrastructure;

public sealed class CurrencyRepository : ICurrencyRepository
{
    private readonly string _provider;
    private readonly string _connectionString;

    public CurrencyRepository(string provider, string connectionString)
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

    public async Task<IReadOnlyList<Currency>> GetCurrenciesAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT currency_code, currency_name_en, currency_name_ar, symbol, fraction_name_en, fraction_name_ar, decimal_places, is_active FROM currencies " +
            (_provider == "PostgreSQL" ? (includeInactive ? "" : "WHERE is_active = TRUE ") : (includeInactive ? "" : "WHERE is_active = 1 ")) +
            "ORDER BY currency_code";
        var result = new List<Currency>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new Currency(
                reader.GetString(0), reader.GetString(1), NullableString(reader, 2), NullableString(reader, 3),
                NullableString(reader, 4), NullableString(reader, 5),
                Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture),
                Convert.ToBoolean(reader.GetValue(7), CultureInfo.InvariantCulture)));
        }
        return result.AsReadOnly();
    }

    public async Task SaveCurrencyAsync(Currency currency, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currency);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO currencies (currency_code, currency_name_en, currency_name_ar, symbol, fraction_name_en, fraction_name_ar, decimal_places, is_active, updated_at)
            VALUES (@code, @nameEn, @nameAr, @symbol, @fractionEn, @fractionAr, @places, @active, CURRENT_TIMESTAMP)
            ON CONFLICT(currency_code) DO UPDATE SET
                currency_name_en=excluded.currency_name_en,
                currency_name_ar=excluded.currency_name_ar,
                symbol=excluded.symbol,
                fraction_name_en=excluded.fraction_name_en,
                fraction_name_ar=excluded.fraction_name_ar,
                decimal_places=excluded.decimal_places,
                is_active=excluded.is_active,
                updated_at=CURRENT_TIMESTAMP
            """;
        Add(command, "@code", currency.Code);
        Add(command, "@nameEn", currency.NameEnglish);
        Add(command, "@nameAr", (object?)currency.NameArabic ?? DBNull.Value);
        Add(command, "@symbol", (object?)currency.Symbol ?? DBNull.Value);
        Add(command, "@fractionEn", (object?)currency.FractionNameEnglish ?? DBNull.Value);
        Add(command, "@fractionAr", (object?)currency.FractionNameArabic ?? DBNull.Value);
        Add(command, "@places", currency.DecimalPlaces);
        Add(command, "@active", _provider == "PostgreSQL" ? currency.IsActive : currency.IsActive ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CurrencyRate>> GetRatesAsync(Guid companyId, string fromCurrency, string toCurrency, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT rate_id, company_id, from_currency, to_currency, rate, buy_rate, sell_rate,
                   min_rate, max_rate, effective_date, source, notes
            FROM currency_rates
            WHERE company_id=@company AND from_currency=@from AND to_currency=@to
            ORDER BY effective_date DESC, rate_id DESC
            """;
        Add(command, "@company", _provider == "PostgreSQL" ? companyId : companyId.ToString("D"));
        Add(command, "@from", fromCurrency.Trim().ToUpperInvariant());
        Add(command, "@to", toCurrency.Trim().ToUpperInvariant());
        var result = new List<CurrencyRate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var storedCompany = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture);
            if (!Guid.TryParse(storedCompany, out var parsedCompany) || parsedCompany != companyId)
                throw new InvalidOperationException("A currency rate belongs to an unexpected company.");
            var effectiveDate = _provider == "PostgreSQL"
                ? reader.GetFieldValue<DateOnly>(9)
                : DateOnly.Parse(Convert.ToString(reader.GetValue(9), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
            result.Add(new CurrencyRate(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture), parsedCompany,
                reader.GetString(2), reader.GetString(3),
                Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture),
                NullableDecimal(reader, 5), NullableDecimal(reader, 6), NullableDecimal(reader, 7), NullableDecimal(reader, 8),
                effectiveDate, NullableString(reader, 10), NullableString(reader, 11)));
        }
        return result.AsReadOnly();
    }

    public async Task SaveRateAsync(CurrencyRateDraft rate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rate);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO currency_rates
                (company_id, from_currency, to_currency, rate, buy_rate, sell_rate, min_rate, max_rate, effective_date, source, notes, created_by)
            VALUES
                (@company, @from, @to, @rate, @buy, @sell, @min, @max, @date, @source, @notes, @actor)
            ON CONFLICT(company_id, from_currency, to_currency, effective_date) DO UPDATE SET
                rate=excluded.rate, buy_rate=excluded.buy_rate, sell_rate=excluded.sell_rate,
                min_rate=excluded.min_rate, max_rate=excluded.max_rate, source=excluded.source,
                notes=excluded.notes, created_by=excluded.created_by
            """;
        Add(command, "@company", _provider == "PostgreSQL" ? rate.CompanyId : rate.CompanyId.ToString("D"));
        Add(command, "@from", rate.FromCurrency);
        Add(command, "@to", rate.ToCurrency);
        Add(command, "@rate", rate.Rate);
        Add(command, "@buy", (object?)rate.BuyRate ?? DBNull.Value);
        Add(command, "@sell", (object?)rate.SellRate ?? DBNull.Value);
        Add(command, "@min", (object?)rate.MinRate ?? DBNull.Value);
        Add(command, "@max", (object?)rate.MaxRate ?? DBNull.Value);
        Add(command, "@date", _provider == "PostgreSQL" ? rate.EffectiveDate : rate.EffectiveDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(command, "@source", (object?)rate.Source ?? DBNull.Value);
        Add(command, "@notes", (object?)rate.Notes ?? DBNull.Value);
        Add(command, "@actor", (object?)rate.CreatedBy ?? DBNull.Value);
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

    private static decimal? NullableDecimal(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
}
