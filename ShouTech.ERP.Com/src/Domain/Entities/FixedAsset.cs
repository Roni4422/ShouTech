namespace ShouTech.Domain.Entities;

/// <summary>Depreciation methods supported by the asset register.</summary>
public static class DepreciationMethods
{
    public const string StraightLine = "StraightLine";
    public const string DecliningBalance = "DecliningBalance";

    public static readonly IReadOnlyList<string> All = new[] { StraightLine, DecliningBalance };
}

/// <summary>Fixed-asset lifecycle statuses.</summary>
public static class AssetStatuses
{
    public const string Active = "Active";
    public const string Disposed = "Disposed";
    public const string FullyDepreciated = "FullyDepreciated";

    public static readonly IReadOnlyList<string> All = new[] { Active, Disposed, FullyDepreciated };
}

/// <summary>A registered fixed asset. Depreciation is computed, never stored.</summary>
public sealed class FixedAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Category { get; set; }
    public DateOnly PurchaseDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public decimal Cost { get; set; }
    public decimal SalvageValue { get; set; }
    public int UsefulLifeYears { get; set; } = 5;
    public string DepreciationMethod { get; set; } = DepreciationMethods.StraightLine;
    public string? Location { get; set; }
    public string Status { get; set; } = AssetStatuses.Active;
    public string? Notes { get; set; }
}
