using System;
using System.Collections.Generic;
using System.Linq;
using ShouTech.Infrastructure;
using Xunit;

namespace Kern.Tests;

public sealed class LandedCostAllocatorTests
{
    private static readonly LandedCostReceiptLine[] Lines =
    {
        new(11, 2m, 100m, 10m, 1m),
        new(12, 3m, 300m, 30m, 3m),
        new(13, 5m, 600m, 60m, 6m)
    };

    [Theory]
    [InlineData("Value", 100m, 10m, 30m, 60m)]
    [InlineData("Quantity", 100m, 20m, 30m, 50m)]
    [InlineData("Weight", 100m, 10m, 30m, 60m)]
    [InlineData("Volume", 100m, 10m, 30m, 60m)]
    public void ProportionalBasesAllocateAndReconcile(string basis, decimal total, decimal first, decimal second, decimal third)
    {
        var result = LandedCostAllocator.Allocate(Lines,
            new[] { new LandedCostCharge("FREIGHT", total, basis) });
        Assert.Equal(3, result.Count);
        Assert.Equal(first, result.Single(x => x.ReceiptLineId == 11).AllocatedBaseAmount);
        Assert.Equal(second, result.Single(x => x.ReceiptLineId == 12).AllocatedBaseAmount);
        Assert.Equal(third, result.Single(x => x.ReceiptLineId == 13).AllocatedBaseAmount);
        Assert.Equal(total, result.Sum(x => x.AllocatedBaseAmount));
    }

    [Fact]
    public void ManualAllocationMustMatchChargeAndOnlyUsesKnownReceiptLines()
    {
        var result = LandedCostAllocator.Allocate(Lines, new[]
        {
            new LandedCostCharge("CUSTOMS", 17.25m, "Manual",
                new Dictionary<long, decimal> { [11] = 2.25m, [13] = 15m })
        });
        Assert.Equal(17.25m, result.Sum(x => x.AllocatedBaseAmount));
        Assert.Equal(2, result.Count);
        Assert.Throws<InvalidOperationException>(() => LandedCostAllocator.Allocate(Lines, new[]
        {
            new LandedCostCharge("CUSTOMS", 17m, "Manual",
                new Dictionary<long, decimal> { [11] = 2m, [99] = 15m })
        }));
    }

    [Fact]
    public void RoundingRemainderIsAbsorbedAndNegativeCreditsRemainNegative()
    {
        var result = LandedCostAllocator.Allocate(
            new[] { new LandedCostReceiptLine(1, 1m, 1m, 1m, 1m),
                    new LandedCostReceiptLine(2, 1m, 1m, 1m, 1m),
                    new LandedCostReceiptLine(3, 1m, 1m, 1m, 1m) },
            new[] { new LandedCostCharge("REBATE", -1m, "Value") });
        Assert.Equal(-1m, result.Sum(x => x.AllocatedBaseAmount));
        Assert.Equal(-0.3334m, result.Single(x => x.ReceiptLineId == 3).AllocatedBaseAmount);
    }

    [Fact]
    public void ManualAllocationCannotInvertChargeSign()
    {
        Assert.Throws<InvalidOperationException>(() => LandedCostAllocator.Allocate(Lines, new[]
        {
            new LandedCostCharge("FREIGHT", 10m, "Manual",
                new Dictionary<long, decimal> { [11] = -5m, [12] = 15m })
        }));
    }

    [Fact]
    public void ZeroAllocationBaseIsRejected()
    {
        var noWeight = new[] { new LandedCostReceiptLine(1, 1m, 0m, 0m, 0m) };
        Assert.Throws<InvalidOperationException>(() => LandedCostAllocator.Allocate(
            noWeight, new[] { new LandedCostCharge("FREIGHT", 10m, "Value") }));
    }
}
