using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>One year of a depreciation schedule.</summary>
public sealed record DepreciationYearRow(int Year, decimal Depreciation, decimal Accumulated, decimal BookValue);

/// <summary>
/// Fixed Assets application service: register validation plus the depreciation
/// engine. Schedules are computed deterministically from the register fields:
/// - StraightLine: (cost - salvage) / life, spread evenly by month.
/// - DecliningBalance: double-declining rate (2 / life) on the opening book
///   value of each year, spread by month, never depreciating below salvage.
/// Accumulated depreciation is pro-rated by whole elapsed months.
/// </summary>
public sealed class AssetService
{
    private readonly IAssetRepository _repository;

    public AssetService(IAssetRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public Task<IReadOnlyList<FixedAsset>> GetAllAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetAllAsync(RequireCompany(companyId), cancellationToken);

    public async Task<Guid> SaveAsync(string companyId, FixedAsset asset, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        Validate(asset);
        asset.Code = asset.Code.Trim();
        asset.Name = asset.Name.Trim();
        await _repository.SaveAsync(companyId, asset, RequireUser(userId), cancellationToken).ConfigureAwait(false);
        return asset.Id;
    }

    public Task DeleteAsync(string companyId, Guid assetId, string userId, CancellationToken cancellationToken = default)
    {
        if (assetId == Guid.Empty)
            throw new ArgumentException("Asset is required.", nameof(assetId));
        return _repository.SoftDeleteAsync(RequireCompany(companyId), assetId, RequireUser(userId), cancellationToken);
    }

    /// <summary>Full yearly schedule over the asset's useful life.</summary>
    public static IReadOnlyList<DepreciationYearRow> BuildSchedule(FixedAsset asset)
    {
        Validate(asset);
        var rows = new List<DepreciationYearRow>();
        var book = asset.Cost;
        var accumulated = 0m;
        for (var year = 1; year <= asset.UsefulLifeYears; year++)
        {
            var depreciation = YearlyDepreciation(asset, book, year);
            accumulated += depreciation;
            book -= depreciation;
            rows.Add(new DepreciationYearRow(year, Round(depreciation), Round(accumulated), Round(book)));
        }
        return rows;
    }

    /// <summary>Accumulated depreciation at a date, pro-rated by whole months.</summary>
    public static decimal AccumulatedDepreciation(FixedAsset asset, DateOnly asOf)
    {
        Validate(asset);
        var months = WholeMonthsBetween(asset.PurchaseDate, asOf);
        if (months <= 0)
            return 0m;
        var maxMonths = asset.UsefulLifeYears * 12;
        if (months > maxMonths)
            months = maxMonths;

        var accumulated = 0m;
        var book = asset.Cost;
        var month = 0;
        while (month < months)
        {
            var yearIndex = month / 12 + 1;
            var monthly = YearlyDepreciation(asset, book, yearIndex) / 12m;
            var monthsThisYear = Math.Min(12, months - month);
            var dep = monthly * monthsThisYear;
            // The final month absorbs rounding so the book value lands exactly on salvage.
            if (month + monthsThisYear >= maxMonths)
                dep = book - asset.SalvageValue;
            accumulated += dep;
            book -= dep;
            month += monthsThisYear;
        }
        return Round(accumulated);
    }

    public static decimal BookValue(FixedAsset asset, DateOnly asOf)
        => Round(asset.Cost - AccumulatedDepreciation(asset, asOf));

    private static decimal YearlyDepreciation(FixedAsset asset, decimal openingBook, int year)
    {
        var depreciable = openingBook - asset.SalvageValue;
        if (depreciable <= 0)
            return 0m;
        if (year >= asset.UsefulLifeYears)
            return depreciable; // final year: land exactly on salvage value
        return asset.DepreciationMethod switch
        {
            DepreciationMethods.StraightLine => (asset.Cost - asset.SalvageValue) / asset.UsefulLifeYears,
            DepreciationMethods.DecliningBalance => Math.Min(openingBook * (2m / asset.UsefulLifeYears), depreciable),
            _ => throw new InvalidOperationException($"Unknown depreciation method '{asset.DepreciationMethod}'.")
        };
    }

    private static int WholeMonthsBetween(DateOnly from, DateOnly to)
    {
        if (to <= from)
            return 0;
        var months = (to.Year - from.Year) * 12 + (to.Month - from.Month);
        if (to.Day < from.Day)
            months--;
        return Math.Max(0, months);
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static void Validate(FixedAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (string.IsNullOrWhiteSpace(asset.Code))
            throw new InvalidOperationException("Asset code is required.");
        if (string.IsNullOrWhiteSpace(asset.Name))
            throw new InvalidOperationException("Asset name is required.");
        if (asset.Cost < 0)
            throw new InvalidOperationException("Asset cost cannot be negative.");
        if (asset.SalvageValue < 0)
            throw new InvalidOperationException("Salvage value cannot be negative.");
        if (asset.SalvageValue > asset.Cost)
            throw new InvalidOperationException("Salvage value cannot exceed cost.");
        if (asset.UsefulLifeYears <= 0)
            throw new InvalidOperationException("Useful life must be at least one year.");
        if (!DepreciationMethods.All.Contains(asset.DepreciationMethod))
            throw new InvalidOperationException($"Unknown depreciation method '{asset.DepreciationMethod}'.");
        if (!AssetStatuses.All.Contains(asset.Status))
            throw new InvalidOperationException($"Unknown asset status '{asset.Status}'.");
    }

    private static string RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
        return companyId;
    }

    private static string RequireUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException("A current user is required for audited writes.");
        return userId;
    }
}
