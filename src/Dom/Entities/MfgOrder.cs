namespace ShouTech.Domain.Entities;

/// <summary>Manufacturing order lifecycle statuses (RS-8 + staged
/// workflow, migration 058). Legacy one-step orders move
/// Draft → Posted → Cancelled. Staged orders move
/// Draft → Confirmed → InProcess → Completed → Closed, with Cancelled
/// reachable from every pre-Closed stage. Both lifecycles share the
/// table, the valuation engine and GlPosting.</summary>
public static class MfgOrderStatuses
{
    public const string Draft = "Draft";
    public const string Confirmed = "Confirmed";
    public const string InProcess = "InProcess";
    public const string Completed = "Completed";
    public const string Closed = "Closed";
    public const string Posted = "Posted";
    public const string Cancelled = "Cancelled";

    public static readonly IReadOnlyList<string> All =
        new[] { Draft, Confirmed, InProcess, Completed, Closed, Posted, Cancelled };

    /// <summary>Statuses the staged workflow can act on (not legacy
    /// Posted, not terminal).</summary>
    public static bool IsStaged(string status)
        => status is Draft or Confirmed or InProcess or Completed;
}

/// <summary>Line kinds on a manufacturing order. RAW lines consume stock
/// on issue; BYPRODUCT lines are extra outputs received at Finish with a
/// cost share (ERPNext by-product model); SCRAP lines record scrapped
/// output at Finish (zero value, scrap warehouse); COST lines carry
/// labour / added-cost amounts absorbed like overhead.</summary>
public static class MfgLineKinds
{
    public const string Raw = "RAW";
    public const string Cost = "COST";
    public const string ByProduct = "BYPRODUCT";
    public const string Scrap = "SCRAP";

    public static readonly IReadOnlyList<string> All = new[] { Raw, Cost, ByProduct, Scrap };
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

    // ---- Staged workflow (migration 058) -------------------------------
    /// <summary>The BOM this order was built from (fetch/explode pins the
    /// version); null for ad-hoc orders typed by hand.</summary>
    public Guid? BomId { get; set; }
    public int? BomVersion { get; set; }
    public DateOnly? PlannedStartDate { get; set; }
    public DateOnly? PlannedEndDate { get; set; }
    /// <summary>Make-to-order link: the sales_documents doc_id + number
    /// of the sales order this production serves.</summary>
    public long? SalesOrderDocId { get; set; }
    public string? SalesOrderNo { get; set; }
    public string? CostCenter { get; set; }
    /// <summary>Split warehouses (codes or id text, the mfg convention).
    /// Null falls back to <see cref="WarehouseCode"/> at posting time.</summary>
    public string? SourceWarehouseCode { get; set; }
    public string? WipWarehouseCode { get; set; }
    public string? TargetWarehouseCode { get; set; }
    public string? ScrapWarehouseCode { get; set; }
    public string InputDocumentKind { get; set; } = "MATERIAL_ISSUE";
    public string OutputDocumentKind { get; set; } = "MATERIAL_RECEIPT";
    /// <summary>Finished quantity actually received so far (header unit).</summary>
    public decimal ProducedQty { get; set; }
    /// <summary>Total cost accumulated into the order (issues at engine
    /// cost + absorbed overhead / COST lines).</summary>
    public decimal AccumulatedCost { get; set; }
    /// <summary>Cost still sitting in WIP for this order: accumulated
    /// minus the pool taken by finishes and the close settlement. The
    /// staged GL keeps the WIP account equal to this per order.</summary>
    public decimal WipBalance { get; set; }
    /// <summary>The WIP remainder settled to the variance account at
    /// Close (positive = extra cost expensed; negative = over-absorbed).</summary>
    public decimal VarianceAmount { get; set; }
    /// <summary>Informational standard variance at Close: actual finished
    /// value minus produced qty x standard unit cost.</summary>
    public decimal StandardVariance { get; set; }

    public List<MfgOrderLine> Lines { get; set; } = new();

    /// <summary>The effective source warehouse for issues.</summary>
    public string? EffectiveSourceWarehouse => SourceWarehouseCode ?? WarehouseCode;
    /// <summary>The effective target warehouse for finished receipts.</summary>
    public string? EffectiveTargetWarehouse => TargetWarehouseCode ?? WarehouseCode;
    /// <summary>The effective scrap warehouse for scrap receipts.</summary>
    public string? EffectiveScrapWarehouse => ScrapWarehouseCode ?? WarehouseCode;
}

/// <summary>One line of a manufacturing order. Qty is the REQUIRED
/// (planned) quantity in the line's unit for RAW lines, the planned
/// output for BYPRODUCT / SCRAP lines, and the cost basis quantity for
/// COST lines. ConsumedQty / ProducedQty track the staged actuals in
/// the same unit, so required-vs-actual is always visible.</summary>
public sealed class MfgOrderLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    /// <summary>Stable display order (migration 058; fixes MFG-9).</summary>
    public int LineNo { get; set; }
    public string LineKind { get; set; } = MfgLineKinds.Raw;
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemName { get; set; }
    public decimal Qty { get; set; }
    public string? UnitCode { get; set; }
    public decimal UnitCost { get; set; }
    public decimal StandardUnitCost { get; set; }
    public decimal Amount { get; set; }
    /// <summary>RAW: quantity actually issued so far (line unit).</summary>
    public decimal ConsumedQty { get; set; }
    /// <summary>BYPRODUCT / SCRAP: quantity actually received so far.</summary>
    public decimal ProducedQty { get; set; }
    /// <summary>Per-line source warehouse override; null = order source.</summary>
    public string? WarehouseCode { get; set; }
    public string? DebitAccountCode { get; set; }
    public string? CreditAccountCode { get; set; }
    public string? CostCenter { get; set; }
    /// <summary>BYPRODUCT only: share (0-100%) of the finish pool this
    /// by-product carries (ERPNext by-product cost allocation).</summary>
    public decimal CostSharePct { get; set; }

    /// <summary>RAW remaining to issue (line unit).</summary>
    public decimal RemainingToIssue => LineKind == MfgLineKinds.Raw ? Qty - ConsumedQty : 0m;
}

/// <summary>One issue instruction inside a staged Issue call: how much of
/// one RAW line to issue now (line unit). Lines not mentioned are not
/// issued in this call — partial issues are the norm.</summary>
public sealed record MfgIssueLine(Guid LineId, decimal Qty);

/// <summary>One receipt instruction inside a staged Finish call for a
/// BYPRODUCT or SCRAP line: how much of that line to receive now.</summary>
public sealed record MfgReceiptLine(Guid LineId, decimal Qty);

/// <summary>The outcome of one staged Issue / Finish / Close action:
/// the new order status, the money that moved, and the real GL document
/// number GlPosting created (null when the action moved no money).</summary>
public sealed record MfgStageResult(
    Guid OrderId, string Status, decimal Amount, string? JournalDocNo,
    decimal WipBalance, decimal ProducedQty);

/// <summary>One recorded staged action on an order (mfg_production_events).</summary>
public sealed record MfgProductionEvent(
    Guid Id, Guid OrderId, string EventType, string EventDate, int Seq,
    decimal Qty, decimal Amount, string? JournalDocNo, string? Notes, string? CreatedBy);
