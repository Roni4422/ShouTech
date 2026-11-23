using System;
using System.Collections.Generic;
using System.Linq;

namespace ShouTech.Infrastructure;

public sealed record LandedCostConsumptionLine(
    long ReceiptLineId,
    decimal ReceivedBaseQuantity,
    decimal RemainingBaseQuantity,
    decimal AllocatedBaseAmount);

public sealed record LandedCostConsumptionSplit(
    long ReceiptLineId,
    decimal InventoryBaseAdjustment,
    decimal ConsumedBaseAdjustment);

/// <summary>
/// Splits a receipt-line landed-cost allocation between stock still on hand
/// and quantities already consumed. The consumed portion is returned as a
/// COGS adjustment so late freight, customs, insurance, and similar charges
/// do not remain capitalized in inventory after the goods have been issued.
/// Callers must persist both legs and post them atomically with the voucher.
/// </summary>
public static class LandedCostConsumptionAllocator
{
    public static IReadOnlyList<LandedCostConsumptionSplit> Split(
        IReadOnlyList<LandedCostConsumptionLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0)
            throw new InvalidOperationException("At least one landed-cost allocation is required.");
        if (lines.Any(x => x.ReceiptLineId <= 0 ||
            x.ReceivedBaseQuantity <= 0m ||
            x.RemainingBaseQuantity < 0m ||
            x.RemainingBaseQuantity > x.ReceivedBaseQuantity))
            throw new InvalidOperationException("Remaining quantity must be between zero and received quantity.");
        if (lines.Select(x => x.ReceiptLineId).Distinct().Count() != lines.Count)
            throw new InvalidOperationException("Receipt line identifiers must be unique.");

        var result = new List<LandedCostConsumptionSplit>(lines.Count);
        foreach (var line in lines.OrderBy(x => x.ReceiptLineId))
        {
            var inventory = line.RemainingBaseQuantity == line.ReceivedBaseQuantity
                ? Round(line.AllocatedBaseAmount)
                : line.RemainingBaseQuantity == 0m
                    ? 0m
                    : Round(line.AllocatedBaseAmount *
                        line.RemainingBaseQuantity / line.ReceivedBaseQuantity);
            var consumed = Round(line.AllocatedBaseAmount - inventory);
            result.Add(new LandedCostConsumptionSplit(
                line.ReceiptLineId, inventory, consumed));
        }

        var expected = Round(lines.Sum(x => x.AllocatedBaseAmount));
        var actual = Round(result.Sum(x => x.InventoryBaseAdjustment + x.ConsumedBaseAdjustment));
        if (actual != expected)
            throw new InvalidOperationException("Landed-cost inventory and consumed-cost adjustments do not reconcile.");
        return result;
    }

    private static decimal Round(decimal value)
        => decimal.Round(value, 4, MidpointRounding.AwayFromZero);
}
