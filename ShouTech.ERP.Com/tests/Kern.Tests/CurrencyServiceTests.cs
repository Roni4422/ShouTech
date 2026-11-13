using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.Application.Services;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
using ShouTech.Infrastructure;
using Xunit;

namespace Kern.Tests;

public sealed class CurrencyServiceTests
{
    [Fact]
    public async Task SaveCurrencyAsync_NormalizesCodeAndEnglishName()
    {
        var repository = new RecordingCurrencyRepository();
        var service = new CurrencyService(repository);

        await service.SaveCurrencyAsync(new Currency(
            " usd ", " US Dollar ", null, "$", "Cent", null, 2, true));

        Assert.NotNull(repository.SavedCurrency);
        Assert.Equal("USD", repository.SavedCurrency.Code);
        Assert.Equal("US Dollar", repository.SavedCurrency.NameEnglish);
    }

    [Theory]
    [InlineData("US")]
    [InlineData("USDD")]
    [InlineData("1SD")]
    [InlineData("عرب")]
    public async Task SaveCurrencyAsync_RejectsInvalidCurrencyCode(string code)
    {
        var service = new CurrencyService(new RecordingCurrencyRepository());

        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveCurrencyAsync(
            new Currency(code, "Currency", null, null, null, null, 2, true)));
    }

    [Fact]
    public async Task SaveCurrencyAsync_RejectsMissingEnglishName()
    {
        var service = new CurrencyService(new RecordingCurrencyRepository());

        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveCurrencyAsync(
            new Currency("USD", " ", null, null, null, null, 2, true)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    public async Task SaveCurrencyAsync_RejectsDecimalPlacesOutsideSupportedRange(int decimalPlaces)
    {
        var service = new CurrencyService(new RecordingCurrencyRepository());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SaveCurrencyAsync(
            new Currency("USD", "US Dollar", null, "$", "Cent", null, decimalPlaces, true)));
    }

    [Fact]
    public async Task CurrencyRepository_PersistsAndReloadsCurrencyAndCompanyRate()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"shoutech-currency-{Guid.NewGuid():N}.db");
        try
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE companies (company_id TEXT PRIMARY KEY);
                    CREATE TABLE currencies (
                        currency_code TEXT PRIMARY KEY COLLATE NOCASE,
                        currency_name_en TEXT NOT NULL,
                        currency_name_ar TEXT,
                        symbol TEXT,
                        fraction_name_en TEXT,
                        fraction_name_ar TEXT,
                        decimal_places INTEGER NOT NULL,
                        is_active INTEGER NOT NULL,
                        created_at TEXT NOT NULL DEFAULT (datetime('now')),
                        updated_at TEXT
                    );
                    CREATE TABLE currency_rates (
                        rate_id INTEGER PRIMARY KEY AUTOINCREMENT,
                        company_id TEXT NOT NULL REFERENCES companies(company_id),
                        from_currency TEXT NOT NULL REFERENCES currencies(currency_code),
                        to_currency TEXT NOT NULL REFERENCES currencies(currency_code),
                        rate NUMERIC NOT NULL,
                        buy_rate NUMERIC,
                        sell_rate NUMERIC,
                        min_rate NUMERIC,
                        max_rate NUMERIC,
                        effective_date TEXT NOT NULL,
                        source TEXT,
                        notes TEXT,
                        created_by TEXT,
                        created_at TEXT NOT NULL DEFAULT (datetime('now')),
                        UNIQUE(company_id, from_currency, to_currency, effective_date)
                    );
                    INSERT INTO companies(company_id) VALUES ('11111111-1111-1111-1111-111111111111');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var repository = new CurrencyRepository("SQLite", connectionString);
            await repository.SaveCurrencyAsync(new Currency(
                "USD", "US Dollar", "Dollar", "$", "Cent", null, 2, true));
            await repository.SaveCurrencyAsync(new Currency(
                "KWD", "Kuwaiti Dinar", null, "KD", "Fils", null, 3, true));

            var currencies = await repository.GetCurrenciesAsync(includeInactive: true);
            Assert.Equal(2, currencies.Count);
            Assert.Contains(currencies, currency => currency.Code == "USD" && currency.NameArabic == "Dollar");
            Assert.Contains(currencies, currency => currency.Code == "KWD" && currency.DecimalPlaces == 3);

            var companyId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            await repository.SaveRateAsync(new CurrencyRateDraft(
                companyId, "USD", "KWD", 0.307m, 0.306m, 0.308m, 0.300m, 0.310m,
                new DateOnly(2026, 11, 13), "Test", "Opening rate", "test-user"));

            var rates = await repository.GetRatesAsync(companyId, "USD", "KWD");
            var rate = Assert.Single(rates);
            Assert.Equal(0.307m, rate.Rate);
            Assert.Equal(0.306m, rate.BuyRate);
            Assert.Equal(0.308m, rate.SellRate);
            Assert.Equal(new DateOnly(2026, 11, 13), rate.EffectiveDate);
            Assert.Equal("Opening rate", rate.Notes);

            await repository.SaveRateAsync(new CurrencyRateDraft(
                companyId, "USD", "KWD", 0.309m, null, null, null, null,
                new DateOnly(2026, 11, 13), "Updated", "Revised rate", "test-user"));

            var updatedRates = await repository.GetRatesAsync(companyId, "USD", "KWD");
            var updatedRate = Assert.Single(updatedRates);
            Assert.Equal(0.309m, updatedRate.Rate);
            Assert.Equal("Revised rate", updatedRate.Notes);
        }
        finally
        {
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }

    private sealed class RecordingCurrencyRepository : ICurrencyRepository
    {
        public Currency? SavedCurrency { get; private set; }

        public Task<IReadOnlyList<Currency>> GetCurrenciesAsync(
            bool includeInactive = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Currency>>(Array.Empty<Currency>());

        public Task SaveCurrencyAsync(Currency currency, CancellationToken cancellationToken = default)
        {
            SavedCurrency = currency;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CurrencyRate>> GetRatesAsync(
            Guid companyId,
            string fromCurrency,
            string toCurrency,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CurrencyRate>>(Array.Empty<CurrencyRate>());

        public Task SaveRateAsync(CurrencyRateDraft rate, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
