using System.Collections.ObjectModel;

namespace ShouTech.Application.Services;

/// <summary>Costing policy used by the perpetual stock valuation engine.</summary>
public enum InventoryValuationMethod
{
    Fifo,
    MovingAverage
}

/// <summary>A remaining FIFO receipt layer. Quantities are in the item's stock unit.</summary>
public sealed record InventoryValuationLayer(decimal Quantity, decimal UnitCost);

/// <summary>
/// Immutable valuation balance for one item in one warehouse. Callers must keep separate
/// states for each company/item/warehouse combination and persist successful movements
/// atomically with their stock ledger and source document.
/// </summary>
public sealed class InventoryValuationState
{
    private InventoryValuationState(
        InventoryValuationMethod method,
        decimal quantityOnHand,
        decimal inventoryValue,
        IEnumerable<InventoryValuationLayer> layers)
    {
        Method = method;
        QuantityOnHand = quantityOnHand;
        InventoryValue = inventoryValue;
        Layers = new ReadOnlyCollection<InventoryValuationLayer>(layers.ToArray());
    }

    public InventoryValuationMethod Method { get; }
    public decimal QuantityOnHand { get; }
    public decimal InventoryValue { get; }
    public decimal AverageUnitCost => QuantityOnHand == 0m ? 0m : InventoryValue / QuantityOnHand;
    public IReadOnlyList<InventoryValuationLayer> Layers { get; }

    /// <summary>Creates a zero-balance state for a new item/warehouse.</summary>
    public static InventoryValuationState Empty(InventoryValuationMethod method)
    {
        EnsureMethod(method);
        return new InventoryValuationState(method, 0m, 0m, Array.Empty<InventoryValuationLayer>());
    }

    /// <summary>
    /// Creates an opening balance. For FIFO, the opening balance becomes one receipt layer;
    /// opening stock with zero value is supported.
    /// </summary>
    public static InventoryValuationState OpeningBalance(
        InventoryValuationMethod method,
        decimal quantity,
        decimal unitCost)
    {
        EnsureMethod(method);
        if (quantity < 0m) throw new ArgumentOutOfRangeException(nameof(quantity), "Opening quantity cannot be negative.");
        if (unitCost < 0m) throw new ArgumentOutOfRangeException(nameof(unitCost), "Unit cost cannot be negative.");
        if (quantity == 0m && unitCost != 0m)
            throw new ArgumentException("A zero opening quantity cannot have a unit cost.", nameof(unitCost));

        var layers = method == InventoryValuationMethod.Fifo && quantity > 0m
            ? new[] { new InventoryValuationLayer(quantity, unitCost) }
            : Array.Empty<InventoryValuationLayer>();
        return new InventoryValuationState(method, quantity, quantity * unitCost, layers);
    }

    internal static InventoryValuationState Create(
        InventoryValuationMethod method,
        decimal quantity,
        decimal value,
        IEnumerable<InventoryValuationLayer> layers)
        => new(method, quantity, value, layers);

    private static void EnsureMethod(InventoryValuationMethod method)
    {
        if (method is not InventoryValuationMethod.Fifo and not InventoryValuationMethod.MovingAverage)
            throw new ArgumentOutOfRangeException(nameof(method));
    }
}

/// <summary>Result of a posted receipt or issue, with its extended inventory cost.</summary>
public sealed record InventoryValuationResult(InventoryValuationState State, decimal MovementCost);

/// <summary>
/// Pure, immutable FIFO and moving-average calculations. This class performs no I/O;
/// callers should apply the returned state and stock movement in the same database transaction.
/// </summary>
public static class InventoryValuationEngine
{
    public static InventoryValuationResult Receive(
        InventoryValuationState state,
        decimal quantity,
        decimal unitCost)
    {
        ArgumentNullException.ThrowIfNull(state);
        RequirePositive(quantity, nameof(quantity));
        if (unitCost < 0m) throw new ArgumentOutOfRangeException(nameof(unitCost), "Unit cost cannot be negative.");

        var cost = quantity * unitCost;
        var nextQuantity = checked(state.QuantityOnHand + quantity);
        var nextValue = checked(state.InventoryValue + cost);
        var layers = state.Method == InventoryValuationMethod.Fifo
            ? state.Layers.Append(new InventoryValuationLayer(quantity, unitCost))
            : Array.Empty<InventoryValuationLayer>();

        return new InventoryValuationResult(
            InventoryValuationState.Create(state.Method, nextQuantity, nextValue, layers), cost);
    }

    public static InventoryValuationResult Issue(InventoryValuationState state, decimal quantity)
    {
        ArgumentNullException.ThrowIfNull(state);
        RequirePositive(quantity, nameof(quantity));
        if (quantity > state.QuantityOnHand)
            throw new InvalidOperationException("Insufficient stock for the requested issue.");

        return state.Method == InventoryValuationMethod.Fifo
            ? IssueFifo(state, quantity)
            : IssueMovingAverage(state, quantity);
    }

    private static InventoryValuationResult IssueMovingAverage(InventoryValuationState state, decimal quantity)
    {
        var movementCost = quantity == state.QuantityOnHand
            ? state.InventoryValue
            : checked(quantity * state.AverageUnitCost);
        var remainingQuantity = state.QuantityOnHand - quantity;
        var remainingValue = remainingQuantity == 0m ? 0m : state.InventoryValue - movementCost;
        return new InventoryValuationResult(
            InventoryValuationState.Create(state.Method, remainingQuantity, remainingValue, Array.Empty<InventoryValuationLayer>()),
            movementCost);
    }

    private static InventoryValuationResult IssueFifo(InventoryValuationState state, decimal quantity)
    {
        var remainingToIssue = quantity;
        var movementCost = 0m;
        var remainingLayers = new List<InventoryValuationLayer>(state.Layers.Count);

        foreach (var layer in state.Layers)
        {
            if (remainingToIssue == 0m)
            {
                remainingLayers.Add(layer);
                continue;
            }

            var taken = Math.Min(layer.Quantity, remainingToIssue);
            movementCost = checked(movementCost + checked(taken * layer.UnitCost));
            remainingToIssue -= taken;
            var layerRemaining = layer.Quantity - taken;
            if (layerRemaining > 0m)
                remainingLayers.Add(new InventoryValuationLayer(layerRemaining, layer.UnitCost));
        }

        // A state with FIFO layers inconsistent with its balance is corrupt. Never silently
        // value an issue using an incomplete set of receipt layers.
        if (remainingToIssue != 0m)
            throw new InvalidOperationException("FIFO valuation layers do not reconcile to the stock quantity.");

        var remainingQuantity = state.QuantityOnHand - quantity;
        var remainingValue = remainingLayers.Sum(layer => checked(layer.Quantity * layer.UnitCost));
        return new InventoryValuationResult(
            InventoryValuationState.Create(state.Method, remainingQuantity, remainingValue, remainingLayers),
            movementCost);
    }

    private static void RequirePositive(decimal value, string parameterName)
    {
        if (value <= 0m) throw new ArgumentOutOfRangeException(parameterName, "Quantity must be greater than zero.");
    }
}
