using System;
using System.Linq;
using ShouTech.Infrastructure;
using Xunit;

namespace Kern.Tests;

public sealed class LandedCostConsumptionAllocatorTests
{
    [Fact]
    public void SplitsAllocationBetweenOnHandAndConsumedQuantities()
    {
        var result = LandedCostConsumptionAllocator.Split(new[]
        {
            new LandedCostConsumptionLine(11, 10m, 10m, 100m),
            new LandedCostConsumptionLine(12, 10m, 5m, 100m),
            new LandedCostConsumptionLine(13, 10m, 0m, 100m)
        }).ToDictionary(x => x.ReceiptLineId);

        Assert.Equal(100m, result[11].InventoryBaseAdjustment);
        Assert.Equal(0m, result[11].ConsumedBaseAdjustment);
        Assert.Equal(50m, result[12].InventoryBaseAdjustment);
        Assert.Equal(50m, result[12].ConsumedBaseAdjustment);
        Assert.Equal(0m, result[13].InventoryBaseAdjustment);
        Assert.Equal(100m, result[13].ConsumedBaseAdjustment);
    }

    [Fact]
    public void PreservesNegativeCreditAndReconcilesRounding()
    {
        var result = LandedCostConsumptionAllocator.Split(new[]
        {
            new LandedCostConsumptionLine(1, 3m, 1m, -1m),
            new LandedCostConsumptionLine(2, 3m, 2m, -1m)
        });

        Assert.Equal(-2m, result.Sum(x => x.InventoryBaseAdjustment + x.ConsumedBaseAdjustment));
        Assert.Equal(-1m, result.Sum(x => x.InventoryBaseAdjustment));
        Assert.Equal(-1m, result.Sum(x => x.ConsumedBaseAdjustment));
    }

    [Fact]
    public void RejectsEmptyDuplicateAndOutOfRangeReceiptLines()
    {
        Assert.Throws<InvalidOperationException>(() =>
            LandedCostConsumptionAllocator.Split(Array.Empty<LandedCostConsumptionLine>()));

        Assert.Throws<InvalidOperationException>(() =>
            LandedCostConsumptionAllocator.Split(new[]
            {
                new LandedCostConsumptionLine(1, 2m, 1m, 10m),
                new LandedCostConsumptionLine(1, 2m, 1m, 10m)
            }));

        Assert.Throws<InvalidOperationException>(() =>
            LandedCostConsumptionAllocator.Split(new[]
            {
                new LandedCostConsumptionLine(1, 2m, 3m, 10m)
            }));
    }
}
