using System;
using System.Collections.Generic;
using System.Linq;

namespace ShouTech.Infrastructure;

public static class LandedCostAllocationBases
{
    public const string ByValue = "Value";
    public const string ByQuantity = "Quantity";
    public const string ByWeight = "Weight";
    public const string ByVolume = "Volume";
    public const string Manual = "Manual";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        new[] { ByValue, ByQuantity, ByWeight, ByVolume, Manual },
        StringComparer.OrdinalIgnoreCase);
}

public sealed record LandedCostReceiptLine(
    long ReceiptLineId,
    decimal BaseQuantity,
    decimal BaseGoodsAmount,
    decimal Weight,
    decimal Volume);

public sealed record LandedCostCharge(
    string ChargeCode,
    decimal BaseAmount,
    string AllocationBasis,
    IReadOnlyDictionary<long, decimal>? ManualAmounts = null);

public sealed record LandedCostAllocation(
    string ChargeCode,
    long ReceiptLineId,
    decimal AllocatedBaseAmount);

/// <summary>
/// Deterministic landed-cost distribution shared by receipt valuation and
/// GL posting. Amounts are in company base currency and rounded to four
/// decimal places; the final eligible line absorbs the rounding remainder.
/// </summary>
public static class LandedCostAllocator
{
    public static IReadOnlyList<LandedCostAllocation> Allocate(
        IReadOnlyList<LandedCostReceiptLine> receiptLines,
        IReadOnlyList<LandedCostCharge> charges)
    {
        ArgumentNullException.ThrowIfNull(receiptLines);
        ArgumentNullException.ThrowIfNull(charges);
        if (receiptLines.Count == 0)
            throw new InvalidOperationException("Landed cost requires at least one receipt line.");
        if (receiptLines.Any(x => x.ReceiptLineId <= 0 || x.BaseQuantity <= 0m ||
            x.BaseGoodsAmount < 0m || x.Weight < 0m || x.Volume < 0m))
            throw new InvalidOperationException("Receipt allocation bases must be non-negative and quantity must be positive.");
        if (receiptLines.Select(x => x.ReceiptLineId).Distinct().Count() != receiptLines.Count)
            throw new InvalidOperationException("Receipt line identifiers must be unique.");

        var lineIds = receiptLines.Select(x => x.ReceiptLineId).ToHashSet();
        var result = new List<LandedCostAllocation>();
        foreach (var charge in charges)
        {
            if (string.IsNullOrWhiteSpace(charge.ChargeCode))
                throw new InvalidOperationException("Every landed-cost charge requires a code.");
            if (!LandedCostAllocationBases.All.Contains(charge.AllocationBasis))
                throw new InvalidOperationException($"Unsupported landed-cost allocation basis '{charge.AllocationBasis}'.");
            if (charge.AllocationBasis.Equals(LandedCostAllocationBases.Manual, StringComparison.OrdinalIgnoreCase))
            {
                AllocateManual(charge, lineIds, result);
                continue;
            }

            var weights = receiptLines.Select(line => (Line: line, Weight: WeightFor(line, charge.AllocationBasis))).ToArray();
            var totalWeight = weights.Sum(x => x.Weight);
            if (totalWeight <= 0m)
                throw new InvalidOperationException($"Charge '{charge.ChargeCode}' has no positive '{charge.AllocationBasis}' allocation base.");
            var eligible = weights.Where(x => x.Weight > 0m).ToArray();
            var allocated = 0m;
            for (var i = 0; i < eligible.Length; i++)
            {
                var amount = i == eligible.Length - 1
                    ? Round(charge.BaseAmount - allocated)
                    : Round(charge.BaseAmount * eligible[i].Weight / totalWeight);
                allocated += amount;
                result.Add(new LandedCostAllocation(charge.ChargeCode, eligible[i].Line.ReceiptLineId, amount));
            }
            if (Round(allocated) != Round(charge.BaseAmount))
                throw new InvalidOperationException($"Charge '{charge.ChargeCode}' allocation did not reconcile.");
        }
        return result;
    }

    private static decimal WeightFor(LandedCostReceiptLine line, string basis)
        => basis.ToUpperInvariant() switch
        {
            "VALUE" => line.BaseGoodsAmount,
            "QUANTITY" => line.BaseQuantity,
            "WEIGHT" => line.Weight,
            "VOLUME" => line.Volume,
            _ => throw new InvalidOperationException("Manual allocation is handled separately.")
        };

    private static void AllocateManual(
        LandedCostCharge charge,
        IReadOnlySet<long> lineIds,
        ICollection<LandedCostAllocation> result)
    {
        var amounts = charge.ManualAmounts
            ?? throw new InvalidOperationException($"Charge '{charge.ChargeCode}' requires manual line allocations.");
        if (amounts.Count == 0 || amounts.Keys.Any(id => !lineIds.Contains(id)))
            throw new InvalidOperationException($"Charge '{charge.ChargeCode}' contains an unknown or empty manual allocation.");
        var rounded = amounts.ToDictionary(x => x.Key, x => Round(x.Value));
        if (charge.BaseAmount >= 0m && rounded.Values.Any(x => x < 0m) ||
            charge.BaseAmount < 0m && rounded.Values.Any(x => x > 0m))
            throw new InvalidOperationException($"Manual allocations for '{charge.ChargeCode}' must have the same sign as the charge.");
        if (Round(rounded.Values.Sum()) != Round(charge.BaseAmount))
            throw new InvalidOperationException($"Manual allocations for '{charge.ChargeCode}' must equal its base amount.");
        foreach (var item in rounded.OrderBy(x => x.Key))
            if (item.Value != 0m)
                result.Add(new LandedCostAllocation(charge.ChargeCode, item.Key, item.Value));
    }

    private static decimal Round(decimal value)
        => decimal.Round(value, 4, MidpointRounding.AwayFromZero);
}
