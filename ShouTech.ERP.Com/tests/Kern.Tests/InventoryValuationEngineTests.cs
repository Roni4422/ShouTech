using ShouTech.Application.Services;
using Xunit;

namespace Kern.Tests;

public sealed class InventoryValuationEngineTests
{
    [Fact]
    public void FifoIssueConsumesOldestReceiptLayersFirst()
    {
        var state = InventoryValuationState.Empty(InventoryValuationMethod.Fifo);
        state = InventoryValuationEngine.Receive(state, 5m, 10m).State;
        state = InventoryValuationEngine.Receive(state, 5m, 14m).State;

        var issue = InventoryValuationEngine.Issue(state, 6m);

        Assert.Equal(64m, issue.MovementCost); // 5 × 10 + 1 × 14
        Assert.Equal(4m, issue.State.QuantityOnHand);
        Assert.Equal(56m, issue.State.InventoryValue);
        Assert.Equal(new InventoryValuationLayer(4m, 14m), Assert.Single(issue.State.Layers));
    }

    [Fact]
    public void MovingAverageReceiptRecalculatesAverageAndIssueUsesIt()
    {
        var state = InventoryValuationState.Empty(InventoryValuationMethod.MovingAverage);
        state = InventoryValuationEngine.Receive(state, 4m, 10m).State;
        state = InventoryValuationEngine.Receive(state, 6m, 15m).State;

        Assert.Equal(130m, state.InventoryValue);
        Assert.Equal(13m, state.AverageUnitCost);
        Assert.Empty(state.Layers);

        var issue = InventoryValuationEngine.Issue(state, 3m);
        Assert.Equal(39m, issue.MovementCost);
        Assert.Equal(7m, issue.State.QuantityOnHand);
        Assert.Equal(91m, issue.State.InventoryValue);
        Assert.Equal(13m, issue.State.AverageUnitCost);
    }

    [Fact]
    public void IssueOfEntireBalanceClearsQuantityAndValue()
    {
        var state = InventoryValuationState.OpeningBalance(InventoryValuationMethod.MovingAverage, 3m, 12.5m);

        var issue = InventoryValuationEngine.Issue(state, 3m);

        Assert.Equal(37.5m, issue.MovementCost);
        Assert.Equal(0m, issue.State.QuantityOnHand);
        Assert.Equal(0m, issue.State.InventoryValue);
        Assert.Equal(0m, issue.State.AverageUnitCost);
    }

    [Fact]
    public void InsufficientStockDoesNotMutateOriginalState()
    {
        var state = InventoryValuationState.OpeningBalance(InventoryValuationMethod.Fifo, 2m, 8m);

        Assert.Throws<InvalidOperationException>(() => InventoryValuationEngine.Issue(state, 3m));
        Assert.Equal(2m, state.QuantityOnHand);
        Assert.Equal(16m, state.InventoryValue);
        Assert.Equal(2m, Assert.Single(state.Layers).Quantity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ReceiptRejectsNonPositiveQuantity(decimal quantity)
    {
        var state = InventoryValuationState.Empty(InventoryValuationMethod.Fifo);
        Assert.Throws<ArgumentOutOfRangeException>(() => InventoryValuationEngine.Receive(state, quantity, 1m));
    }

    [Fact]
    public void OpeningBalanceRejectsNegativeValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InventoryValuationState.OpeningBalance(InventoryValuationMethod.Fifo, -1m, 1m));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InventoryValuationState.OpeningBalance(InventoryValuationMethod.MovingAverage, 1m, -1m));
    }
}
