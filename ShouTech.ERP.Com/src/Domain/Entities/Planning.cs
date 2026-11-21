namespace ShouTech.Domain.Entities;

/// <summary>Planned-order lifecycle (migration 058, MRP-lite planning).</summary>
public static class PlannedOrderStatuses
{
    public const string Planned = "Planned";
    public const string Converted = "Converted";
    public const string Cancelled = "Cancelled";
    public static readonly IReadOnlyList<string> All = new[] { Planned, Converted, Cancelled };
}

/// <summary>Purchase-suggestion lifecycle (migration 058).</summary>
public static class PurchaseSuggestionStatuses
{
    public const string Suggested = "Suggested";
    public const string Converted = "Converted";
    public const string Cancelled = "Cancelled";
    public static readonly IReadOnlyList<string> All = new[] { Suggested, Converted, Cancelled };
}

/// <summary>One planning run (MRP-lite): demand from open sales orders and
/// reorder-point shortages, netted against stock and incoming purchase
/// orders, exploded through active BOMs.</summary>
public sealed class PlanningRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RunDate { get; set; } = string.Empty;
    public string Status { get; set; } = "Completed";
    public string? Notes { get; set; }
    public List<PlannedOrder> PlannedOrders { get; set; } = new();
    public List<PurchaseSuggestion> Suggestions { get; set; } = new();
}

/// <summary>A planned production order: build Qty of a material that has
/// an active BOM. Converts into a real Draft manufacturing order whose
/// lines are the BOM explosion.</summary>
public sealed class PlannedOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public string MaterialCode { get; set; } = string.Empty;
    public string? MaterialName { get; set; }
    public decimal Qty { get; set; }
    public string UnitCode { get; set; } = "PCS";
    public Guid? BomId { get; set; }
    public string? DueDate { get; set; }
    /// <summary>Why planning wants this built (SalesOrder / ReorderPoint /
    /// ComponentShortage), kept for the planning window's audit column.</summary>
    public string? Source { get; set; }
    public string Status { get; set; } = PlannedOrderStatuses.Planned;
    public Guid? ConvertedOrderId { get; set; }
}

/// <summary>A purchase suggestion for a leaf component: the net shortage
/// after stock and incoming purchase orders. Converts into a real Draft
/// purchase order through OrderDocumentRepository (the existing document
/// path — a commitment document with no GL/stock effects until posted
/// through the normal chain).</summary>
public sealed class PurchaseSuggestion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public string MaterialCode { get; set; } = string.Empty;
    public string? MaterialName { get; set; }
    public decimal Qty { get; set; }
    public string? UnitCode { get; set; }
    public string? Source { get; set; }
    public string Status { get; set; } = PurchaseSuggestionStatuses.Suggested;
    public long? ConvertedDocId { get; set; }
    public string? ConvertedDocNo { get; set; }
}

/// <summary>One row of a feasibility answer (can we build X?): a leaf
/// requirement against availability. Feasibility reads the SAME
/// explosion planning uses, so the two can never disagree.</summary>
public sealed record FeasibilityRow(
    string MaterialCode,
    string? MaterialName,
    decimal RequiredBaseQty,
    decimal OnHandBaseQty,
    decimal IncomingBaseQty,
    string? UnitCode)
{
    public decimal ShortageBaseQty => Math.Max(0m, RequiredBaseQty - OnHandBaseQty - IncomingBaseQty);
    public bool IsAvailable => ShortageBaseQty <= 0m;
}

/// <summary>The feasibility answer for one finished material + quantity.</summary>
public sealed record FeasibilityResult(
    string MaterialCode, decimal Qty, IReadOnlyList<FeasibilityRow> Rows)
{
    public bool CanBuild => Rows.All(r => r.IsAvailable);
}
