namespace ShouTech.Domain.Entities;

public sealed record Currency(
    string Code,
    string NameEnglish,
    string? NameArabic,
    string? Symbol,
    string? FractionNameEnglish,
    string? FractionNameArabic,
    int DecimalPlaces,
    bool IsActive);

public sealed record CurrencyRate(
    long RateId,
    Guid CompanyId,
    string FromCurrency,
    string ToCurrency,
    decimal Rate,
    decimal? BuyRate,
    decimal? SellRate,
    decimal? MinRate,
    decimal? MaxRate,
    DateOnly EffectiveDate,
    string? Source,
    string? Notes);

public sealed record CurrencyRateDraft(
    Guid CompanyId,
    string FromCurrency,
    string ToCurrency,
    decimal Rate,
    decimal? BuyRate,
    decimal? SellRate,
    decimal? MinRate,
    decimal? MaxRate,
    DateOnly EffectiveDate,
    string? Source,
    string? Notes,
    string? CreatedBy);
