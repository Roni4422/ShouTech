using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

public sealed class CurrencyService
{
    private readonly ICurrencyRepository _repository;

    public CurrencyService(ICurrencyRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public Task<IReadOnlyList<Currency>> GetCurrenciesAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
        => _repository.GetCurrenciesAsync(includeInactive, cancellationToken);

    public async Task SaveCurrencyAsync(Currency currency, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currency);
        var code = currency.Code?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(code) || code.Length != 3 || code.Any(c => c is < 'A' or > 'Z'))
            throw new ArgumentException("Currency code must contain exactly three ASCII letters.", nameof(currency));
        if (string.IsNullOrWhiteSpace(currency.NameEnglish))
            throw new ArgumentException("An English currency name is required.", nameof(currency));
        if (currency.DecimalPlaces is < 0 or > 6)
            throw new ArgumentOutOfRangeException(nameof(currency), "Currency decimal places must be between zero and six.");

        await _repository.SaveCurrencyAsync(currency with { Code = code, NameEnglish = currency.NameEnglish.Trim() }, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SaveRateAsync(CurrencyRateDraft rate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rate);
        if (rate.CompanyId == Guid.Empty)
            throw new ArgumentException("Company is required.", nameof(rate));
        var from = NormalizeCode(rate.FromCurrency, nameof(rate.FromCurrency));
        var to = NormalizeCode(rate.ToCurrency, nameof(rate.ToCurrency));
        if (from == to)
            throw new ArgumentException("Source and target currencies must be different.", nameof(rate));
        if (rate.Rate <= 0 || !PositiveOrNull(rate.BuyRate) || !PositiveOrNull(rate.SellRate) ||
            !PositiveOrNull(rate.MinRate) || !PositiveOrNull(rate.MaxRate))
            throw new ArgumentOutOfRangeException(nameof(rate), "Exchange rates must be greater than zero.");
        if (rate.MinRate.HasValue && rate.MaxRate.HasValue && rate.MinRate > rate.MaxRate)
            throw new ArgumentException("Minimum rate cannot exceed maximum rate.", nameof(rate));

        await _repository.SaveRateAsync(rate with { FromCurrency = from, ToCurrency = to }, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string NormalizeCode(string value, string parameterName)
    {
        var code = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(code) || code.Length != 3 || code.Any(c => c is < 'A' or > 'Z'))
            throw new ArgumentException("Currency code must contain exactly three ASCII letters.", parameterName);
        return code;
    }

    private static bool PositiveOrNull(decimal? value) => value is null or > 0m;
}
