using System.Globalization;

namespace ShouTech.Infrastructure;

/// <summary>
/// One open row of the FIFO/LIFO layer queue (stock_cost_layers,
/// migration 053). QtyRemaining may be negative: a negative layer is the
/// ERPNext-style deficit record created when an issue exceeds the stock
/// on hand for a material that allows negative stock; the next receipt
/// offsets it before opening a new layer.
/// </summary>
public sealed class CostLayerState
{
    public long LayerId { get; set; }
    public string LayerDate { get; set; } = string.Empty;
    public decimal QtyIn { get; set; }
    public decimal QtyRemaining { get; set; }
    public decimal UnitCost { get; set; }
}

/// <summary>
/// The complete valuation state of one (company, warehouse, material):
/// quantity, stock value and the layer queue. The database engine loads
/// it, <see cref="ValuationMath"/> mutates it, and the engine writes the
/// result back — so posting and the Recalculate Costs replay run the
/// exact same math (Fix Phase Step 6, Phase B design section 1).
/// </summary>
public sealed class ValuationState
{
    public decimal Quantity { get; set; }
    public decimal Value { get; set; }
    public List<CostLayerState> Layers { get; } = new();

    /// <summary>The current display rate: value over positive quantity,
    /// 0 when nothing (or a deficit) is on hand.</summary>
    public decimal DisplayRate => Quantity > 0 ? ValuationMath.RoundRate(Value / Quantity) : 0m;
}

public sealed record LayerConsumption(long LayerId, decimal Qty, decimal UnitCost, decimal Amount);

public sealed record IssueOutcome(
    decimal CostAmount, decimal ValuationRate,
    IReadOnlyList<LayerConsumption> Consumed, bool UsedFallback);

public sealed record ReceiptOutcome(decimal CostAmount, decimal ValuationRate);

/// <summary>
/// Pure stock-valuation math for the five material cost methods
/// (materials.cost_method): WeightedAvg, FIFO, LIFO, Standard,
/// LastPurchase. No database access — Kern.Tests pins this logic
/// directly, and StockValuationEngine / StockRecalculationService are
/// thin persistence shells over it.
///
/// Invariants maintained on every call:
///   * state.Value changes by exactly the returned CostAmount, so the
///     sum of movement cost_amounts always equals the balance value;
///   * an issue never values at a selling price: its rate comes from
///     the balance / layer queue / the method's fallback chain only;
///   * a receipt enters at the rate the caller resolved (actual acquisition
///     cost, an original-movement rate for a linked return, or the transfer
///     source rate); Standard method applies its standard rate to issues,
///     leaving acquisition variance visible in the remaining stock value.
/// </summary>
public static class ValuationMath
{
    public const string WeightedAvg = "WeightedAvg";
    public const string Fifo = "FIFO";
    public const string Lifo = "LIFO";
    public const string Standard = "Standard";
    public const string LastPurchase = "LastPurchase";

    public static string NormalizeMethod(string? method) => (method ?? string.Empty).Trim() switch
    {
        var m when string.Equals(m, Fifo, StringComparison.OrdinalIgnoreCase) => Fifo,
        var m when string.Equals(m, Lifo, StringComparison.OrdinalIgnoreCase) => Lifo,
        var m when string.Equals(m, Standard, StringComparison.OrdinalIgnoreCase) => Standard,
        var m when string.Equals(m, LastPurchase, StringComparison.OrdinalIgnoreCase) => LastPurchase,
        _ => WeightedAvg
    };

    public static bool UsesLayers(string method) => method is Fifo or Lifo;

    public static decimal RoundRate(decimal value) => decimal.Round(value, 6, MidpointRounding.AwayFromZero);
    public static decimal RoundMoney(decimal value) => decimal.Round(value, 4, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Applies a receipt of <paramref name="qty"/> at
    /// <paramref name="rate"/> (already resolved by the caller). For the
    /// layer methods the receipt first offsets any negative (deficit)
    /// layers — the value delta then includes the revaluation of the
    /// deficit from its provisional rate to the actual receipt rate —
    /// and any remainder opens a new layer at the receipt rate.
    /// </summary>
    public static ReceiptOutcome Receipt(
        ValuationState state, string method, decimal qty, decimal rate, string layerDate)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (qty <= 0) throw new ArgumentOutOfRangeException(nameof(qty), "Receipt quantity must be positive.");
        rate = RoundRate(Math.Max(rate, 0m));
        var valueBefore = state.Value;
        decimal costAmount;

        if (UsesLayers(method))
        {
            var remaining = qty;
            var delta = 0m;
            foreach (var layer in state.Layers.Where(l => l.QtyRemaining < 0).ToList())
            {
                if (remaining <= 0) break;
                var offset = Math.Min(remaining, -layer.QtyRemaining);
                layer.QtyRemaining += offset;
                // The deficit was provisionally valued at the negative
                // layer's rate; settling it at the receipt rate moves the
                // value by offset x (negative rate - receipt rate) on top
                // of the plain receipt value.
                delta += offset * (layer.UnitCost - rate);
                remaining -= offset;
            }
            if (remaining > 0)
            {
                state.Layers.Add(new CostLayerState
                {
                    LayerId = 0,
                    LayerDate = layerDate,
                    QtyIn = remaining,
                    QtyRemaining = remaining,
                    UnitCost = rate
                });
            }
            costAmount = RoundMoney(qty * rate + delta);
        }
        else
        {
            costAmount = RoundMoney(qty * rate);
        }

        state.Quantity += qty;
        state.Value = valueBefore + costAmount;
        return new ReceiptOutcome(costAmount, rate);
    }

    /// <summary>
    /// Applies an issue of <paramref name="qty"/>. Layer methods consume
    /// the queue (oldest first for FIFO, newest first for LIFO;
    /// <paramref name="consumeNewestFirst"/> lets a FIFO stock-count
    /// shortage consume newest-first per the Phase B design). Other
    /// methods value at <paramref name="nonLayerRate"/> (the moving
    /// average / standard / last-purchase rate the caller resolved).
    /// A deficit beyond the available quantity is valued at
    /// <paramref name="fallbackRate"/> and, for layer methods, recorded
    /// as a negative layer.
    /// </summary>
    public static IssueOutcome Issue(
        ValuationState state, string method, decimal qty,
        decimal nonLayerRate, decimal fallbackRate,
        string layerDate, bool consumeNewestFirst = false)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (qty <= 0) throw new ArgumentOutOfRangeException(nameof(qty), "Issue quantity must be positive.");
        var valueBefore = state.Value;
        var consumed = new List<LayerConsumption>();
        var usedFallback = false;
        decimal costAmount;
        decimal rate;

        if (UsesLayers(method))
        {
            var newestFirst = method == Lifo || consumeNewestFirst;
            var ordered = newestFirst
                ? state.Layers.Where(l => l.QtyRemaining > 0).OrderByDescending(l => l.LayerDate, StringComparer.Ordinal).ThenByDescending(l => l.LayerId).ToList()
                : state.Layers.Where(l => l.QtyRemaining > 0).OrderBy(l => l.LayerDate, StringComparer.Ordinal).ThenBy(l => l.LayerId).ToList();

            var remaining = qty;
            var total = 0m;
            foreach (var layer in ordered)
            {
                if (remaining <= 0) break;
                var take = Math.Min(remaining, layer.QtyRemaining);
                layer.QtyRemaining -= take;
                var amount = take * layer.UnitCost;
                total += amount;
                consumed.Add(new LayerConsumption(layer.LayerId, take, layer.UnitCost, amount));
                remaining -= take;
            }
            if (remaining > 0)
            {
                // Deficit: no (or not enough) layers. Valued at the
                // fallback chain and recorded as a negative layer so the
                // next receipt settles it (ERPNext queue behaviour).
                usedFallback = true;
                var deficitRate = RoundRate(fallbackRate);
                total += remaining * deficitRate;
                state.Layers.Add(new CostLayerState
                {
                    LayerId = 0,
                    LayerDate = layerDate,
                    QtyIn = 0,
                    QtyRemaining = -remaining,
                    UnitCost = deficitRate
                });
                consumed.Add(new LayerConsumption(0, remaining, deficitRate, remaining * deficitRate));
            }
            // An issue that empties the stock absorbs any rounding
            // residue so the balance value lands on exactly zero.
            costAmount = state.Quantity - qty == 0 && state.Quantity > 0
                ? RoundMoney(valueBefore)
                : RoundMoney(total);
            rate = RoundRate(costAmount / qty);
        }
        else
        {
            rate = RoundRate(nonLayerRate);
            if (state.Quantity <= 0 && rate == 0)
            {
                rate = RoundRate(fallbackRate);
                usedFallback = rate != 0 || fallbackRate != 0;
            }
            else if (state.Quantity < qty)
            {
                // Partially covered by stock on hand: the covered part
                // values at the current rate; the engine's fallback only
                // applies when there is no rate at all (handled above).
                // A deficit under WeightedAvg keeps the current average
                // (the deficit is absorbed by the next receipt through
                // the value identity), which is the design's rule.
            }
            costAmount = state.Quantity - qty == 0 && state.Quantity > 0
                ? RoundMoney(valueBefore)
                : RoundMoney(qty * rate);
            rate = RoundRate(costAmount / qty);
        }

        state.Quantity -= qty;
        state.Value = valueBefore - costAmount;
        if (state.Quantity == 0) state.Value = 0m;
        return new IssueOutcome(costAmount, rate, consumed, usedFallback);
    }
}

/// <summary>Shared text helpers for the valuation layer.</summary>
internal static class ValuationText
{
    public static decimal ToDecimal(object? value)
        => value is null or DBNull ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    public static string? ToText(object? value)
        => value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
}
