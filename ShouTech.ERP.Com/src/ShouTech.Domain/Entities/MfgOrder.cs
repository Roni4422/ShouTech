namespace ShouTech.Domain.Entities;

/// <summary>Manufacturing order lifecycle statuses (RS-8).</summary>
public static class MfgOrderStatuses
{
    public const string Draft = "Draft";
    public const string Posted = "Posted";
    public const string Cancelled = "Cancelled";

    public static readonly IReadOnlyList<string> All = new[] { Draft, Posted, Cancelled };
}

/// <summary>Line kinds on a manufacturing order. Only RAW lines consume stock.</summary>
public static class MfgLineKinds
{
    public const string Raw = "RAW";
    public const string Cost = "COST";

    public static readonly IReadOnlyList<string> All = new[] { Raw, Cost };
}

/// <summary>
/// A manufacturing order: produces Qty of ItemCode by consuming its RAW lines.
/// TotalCost / UnitCost are computed at posting time from the actual line
/// amounts plus overhead; StandardUnitCost is the snapshot taken at save time
/// and feeds the variance reports.
/// </summary>
public sealed class MfgOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int DocNo { get; set; }
    public DateOnly DocDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemName { get; set; }
    public decimal Qty { get; set; } = 1m;
    public string UnitCode { get; set; } = "PCS";
    public string? WarehouseCode { get; set; }
    public decimal OverheadAmount { get; set; }
    public decimal StandardUnitCost { get; set; }
    public decimal TotalCost { get; set; }
    public decimal UnitCost { get; set; }
    public string Status { get; set; } = MfgOrderStatuses.Draft;
    public string? Notes { get; set; }
    public List<MfgOrderLine> Lines { get; set; } = new();
}

/// <summary>One raw-material (or added-cost) line of a manufacturing order.</summary>
public sealed class MfgOrderLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public string LineKind { get; set; } = MfgLineKinds.Raw;
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemName { get; set; }
    public decimal Qty { get; set; }
    public string? UnitCode { get; set; }
    public decimal UnitCost { get; set; }
    public decimal StandardUnitCost { get; set; }
    public decimal Amount { get; set; }
}
