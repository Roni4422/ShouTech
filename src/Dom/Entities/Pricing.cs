using System.Collections.Generic;

namespace ShouTech.Domain.Entities;

/// <summary>Which resolution level produced a price (provenance).</summary>
public static class PriceSources
{
    public const string Contract = "Contract";
    public const string CustomerPrice = "CustomerPrice";
    public const string PriceListTier = "PriceListTier";
    public const string TierColumn = "TierColumn";
    public const string MaterialCard = "MaterialCard";
    public const string None = "None";
}

/// <summary>
/// The resolved selling price for one (party, material, qty) request:
/// the unit price, which level produced it, the automatic discount %
/// from discount_rules, and the BXGY free quantity earned by the paid
/// quantity (of <see cref="BonusMaterialCode"/>, the material itself
/// when empty).
/// </summary>
public sealed record PriceResolution(
    decimal Price,
    string Source,
    decimal DiscountPct,
    decimal BonusFreeQty,
    string? BonusMaterialCode,
    string? PriceTier);

/// <summary>One document line as the price guard sees it.</summary>
public sealed record PriceGuardLine(
    int LineNo,
    long? MaterialId,
    string? MaterialCode,
    decimal Qty,
    decimal UnitPrice,
    decimal DiscountPct,
    bool IsBonusFree = false);

/// <summary>
/// Authorized price/discount override context (W7a), mirroring
/// <see cref="CreditOverrideContext"/>: the flags are supplied by the
/// caller ONLY when the acting user holds the matching permission
/// (SALES.PRICE.OVERRIDE for SAL-7018/SAL-7019, SALES.DISCOUNT.OVERRIDE
/// for SAL-7020). Every authorized breach is audited in
/// spr_price_overrides (migration 099) inside the caller's transaction.
/// </summary>
public sealed record PriceOverrideContext(
    string Actor, bool PriceOverride, bool DiscountOverride,
    string? DocKind = null, string? DocNo = null, long? DocId = null);
