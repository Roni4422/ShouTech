using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

public interface ICurrencyRepository
{
    Task<IReadOnlyList<Currency>> GetCurrenciesAsync(bool includeInactive = false, CancellationToken cancellationToken = default);
    Task SaveCurrencyAsync(Currency currency, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CurrencyRate>> GetRatesAsync(Guid companyId, string fromCurrency, string toCurrency, CancellationToken cancellationToken = default);
    Task SaveRateAsync(CurrencyRateDraft rate, CancellationToken cancellationToken = default);
}
