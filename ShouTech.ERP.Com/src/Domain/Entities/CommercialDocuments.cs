using System;
using System.Collections.Generic;

namespace ShouTech.Domain.Entities;

/// <summary>Document kind codes stored in sales_documents.doc_kind / purchase_documents.doc_kind.</summary>
public static class CommercialDocKinds
{
    public const string SalesQuotation = "QUO";
    public const string SalesOrder = "SO";
    public const string DeliveryNote = "DLV";
    public const string SalesInvoice = "SAL";
    public const string RequestForQuotation = "RFQ";
    public const string SupplierQuotation = "SQ";
    public const string PurchaseOrder = "PO";
    public const string PurchaseReceipt = "RCV";
    public const string PurchaseReceiptReturn = "RCR";
    public const string PurchaseInvoice = "PUR";
    public const string PurchaseReturn = "PRT";
    public const string PurchaseRequisition = "PRQ";
}

/// <summary>
/// Approval states stored in sales_documents.approval_state (migration 098,
/// sales cycle W23). The workflow is Draft document + approval_state moving
/// NotRequired → Pending → Approved / Rejected, and is enforced at posting
/// ONLY when the company enables it in sau_sales_settings (default OFF).
/// </summary>
public static class SalesApprovalState
{
    public const string NotRequired = "NotRequired";
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
}

/// <summary>Actions recorded in sau_approval_history (migration 098).</summary>
public static class SalesApprovalAction
{
    public const string Submitted = "Submitted";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Amended = "Amended";
}

/// <summary>Statuses stored in the commercial document status column (TEXT).</summary>
public static class CommercialDocumentStatus
{
    public const string Draft = "Draft";
    public const string Posted = "Posted";
    public const string Closed = "Closed";
    public const string Cancelled = "Cancelled";
    public const string Lost = "Lost";
    public const string Expired = "Expired";
}

/// <summary>
/// Derived fulfilment status of a posted sales order (ERPNext pattern):
/// computed from the stored/computed per_delivered and per_billed
/// percentages, never persisted as a status value of its own. Any document
/// that is not a posted sales order keeps its raw stored status.
/// </summary>
public static class SalesChainDerivedStatus
{
    public const string ToDeliverAndBill = "To Deliver and Bill";
    public const string ToDeliver = "To Deliver";
    public const string ToBill = "To Bill";
    public const string Completed = "Completed";

    public static string Derive(string docKind, string status, decimal perDelivered, decimal perBilled)
    {
        if (docKind != CommercialDocKinds.SalesOrder ||
            status != CommercialDocumentStatus.Posted)
            return status;
        var delivered = perDelivered >= 100m;
        var billed = perBilled >= 100m;
        if (delivered && billed) return Completed;
        if (delivered) return ToBill;
        if (billed) return ToDeliver;
        return ToDeliverAndBill;
    }
}

/// <summary>An immutable line of a commercial document as stored in the database.</summary>
public sealed record CommercialLineSnapshot(
    long LineId,
    int LineNo,
    long? MaterialId,
    string? MaterialCode,
    string Description,
    string? Unit,
    decimal Qty,
    decimal UnitPrice,
    decimal DiscountPct,
    decimal TaxRate,
    decimal LineSubtotal,
    decimal LineTax,
    decimal LineTotal,
    long? WarehouseId,
    // W5 (migration 102): explicit source attribution for lines drawn
    // from a source document when line_no alone cannot identify it —
    // consolidated invoice lines carry the delivery they came from and
    // that delivery's line number. Null on standalone and legacy
    // single-source chain lines (line_no keeps the source identity).
    long? SourceDocId = null,
    int? SourceLineNo = null);

/// <summary>An immutable commercial document header with its lines.</summary>
public sealed record CommercialDocumentSnapshot(
    long DocId,
    Guid CompanyId,
    string DocKind,
    string DocNo,
    DateOnly DocDate,
    long? PartyId,
    string? PartyName,
    long? WarehouseId,
    string CurrencyCode,
    decimal ExchangeRate,
    decimal Subtotal,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal GrandTotal,
    decimal PaidAmount,
    string Status,
    string? Notes,
    IReadOnlyList<CommercialLineSnapshot> Lines,
    string? PartyCode = null,
    string? BranchName = null,
    string? SalesRepName = null,
    string? CostCenter = null,
    string? WarehouseName = null,
    DateOnly? DeliveryDate = null,
    DateOnly? ValidTill = null,
    decimal ExtraAmount = 0m,
    decimal PerDelivered = 0m,
    decimal PerBilled = 0m,
    string? CancelReason = null,
    string? CancelledBy = null,
    string? CancelledAt = null,
    decimal DeliveryCost = 0m,
    int RevisionNo = 1,
    string ApprovalState = SalesApprovalState.NotRequired,
    // W5 (migration 102): export invoice header fields. IsExport flags
    // an export sales invoice; the rest are the shipment/customs facts
    // the invoice carries. Null/false on non-export documents.
    bool IsExport = false,
    string? Incoterm = null,
    string? PortOfLoading = null,
    string? PortOfDischarge = null,
    string? CountryOfOrigin = null,
    string? CustomsRef = null,
    string? VesselFlight = null,
    decimal? GrossWeight = null,
    decimal? NetWeight = null)
{
    /// <summary>Derived chain status (sales orders); the raw status for every other document.</summary>
    public string DerivedStatus => SalesChainDerivedStatus.Derive(DocKind, Status, PerDelivered, PerBilled);
}

/// <summary>
/// One preserved prior version of a sales document (migration 098,
/// sau_document_revisions): the full header + lines as a single JSON
/// snapshot, captured in the amendment transaction BEFORE the document
/// was rewritten. <see cref="RevisionNo"/> is the revision the snapshot
/// preserves (the document's revision after the amendment is +1).
/// Revisions are read-only history.
/// </summary>
public sealed record DocumentRevisionSnapshot(
    Guid RevisionId,
    Guid CompanyId,
    long DocId,
    string DocKind,
    int RevisionNo,
    string SnapshotJson,
    string? ChangeReason,
    string CreatedBy,
    string CreatedAt);

/// <summary>One approval-workflow event (migration 098, sau_approval_history).</summary>
public sealed record ApprovalHistoryEntry(
    Guid HistoryId,
    Guid CompanyId,
    long DocId,
    string DocKind,
    string Action,
    string FromState,
    string ToState,
    string Actor,
    string? Reason,
    string CreatedAt);

/// <summary>Quantity available for return from one posted goods-receipt line.</summary>
public sealed record PurchaseReceiptReturnableLine(
    long ReceiptLineId,
    int LineNo,
    long? MaterialId,
    string? MaterialCode,
    string Description,
    string? Unit,
    decimal ReceivedQty,
    decimal InvoicedQty,
    decimal ReturnedQty,
    decimal ReturnableQty,
    decimal UnitPrice,
    decimal DiscountPct,
    long? WarehouseId);

/// <summary>Identifies one source document of a chain conversion
/// (kind + id), without the link row's own identity.</summary>
public sealed record CommercialDocumentSourceRef(
    string SourceDocKind,
    long SourceDocId);

/// <summary>A cross-document chain link (see dba document_links table).</summary>
public sealed record CommercialDocumentLink(
    long LinkId,
    Guid CompanyId,
    string SourceDocKind,
    long SourceDocId,
    string TargetDocKind,
    long TargetDocId);

/// <summary>Batch allocation captured for a commercial receipt line, in the line's entered unit.</summary>
public sealed record CommercialBatchAllocationDraft(
    string BatchNo, decimal Qty, string? ExpiryDate = null,
    string? MfgDate = null, string? SupplierBatchNo = null);

/// <summary>Serial identity captured for a commercial receipt line.</summary>
public sealed record CommercialSerialSelectionDraft(
    string SerialNo, string? BatchNo = null, string? WarrantyExpiry = null);

/// <summary>A new commercial document line to persist.</summary>
public sealed record CommercialLineDraft(
    long? MaterialId,
    string? MaterialCode,
    string Description,
    string? Unit,
    decimal Qty,
    decimal UnitPrice,
    decimal DiscountPct = 0m,
    decimal TaxRate = 0m,
    long? WarehouseId = null,
    int? SourceLineNo = null,
    IReadOnlyList<CommercialBatchAllocationDraft>? BatchAllocations = null,
    IReadOnlyList<CommercialSerialSelectionDraft>? Serials = null,
    bool AllowExpiredBatchIssue = false,
    // W5 (migration 102): explicit source attribution, set together —
    // the source document this line is drawn from and that document's
    // line number. Used by consolidated invoice lines, whose persisted
    // line_no is sequential (SourceLineNo stays null) because two
    // source deliveries can both have a line 1. Single-source chain
    // conversions leave both null and keep the line_no identity.
    long? SourceDocId = null,
    int? SourceRefLineNo = null);

/// <summary>A new commercial document header to persist with its lines.</summary>
public sealed record CommercialDocumentDraft(
    Guid CompanyId,
    string DocKind,
    string DocNo,
    DateOnly DocDate,
    long? PartyId,
    string? PartyName,
    long? WarehouseId,
    string CurrencyCode,
    decimal ExchangeRate,
    string? Notes,
    IReadOnlyList<CommercialLineDraft> Lines,
    string? PartyCode = null,
    DateOnly? DeliveryDate = null,
    DateOnly? ValidTill = null,
    string? CostCenter = null,
    string? BranchName = null,
    string? SalesRepName = null,
    string? WarehouseName = null,
    decimal DiscountAmount = 0m,
    decimal ExtraAmount = 0m,
    decimal DeliveryCost = 0m,
    // W7c (migration 101): a sales order may draw down against a sales
    // contract (sct_contracts.contract_id, stored as TEXT Guid).
    string? ContractId = null,
    // W5 (migration 102): export invoice header fields (see the
    // snapshot record for the semantics).
    bool IsExport = false,
    string? Incoterm = null,
    string? PortOfLoading = null,
    string? PortOfDischarge = null,
    string? CountryOfOrigin = null,
    string? CustomsRef = null,
    string? VesselFlight = null,
    decimal? GrossWeight = null,
    decimal? NetWeight = null,
    // Y3: explicit payment due date for purchase invoices (migration 094,
    // renumbered 109). Null means "derive at insert": doc date + the
    // supplier's credit_days.
    DateOnly? DueDate = null);

/// <summary>Header-only projection used by document search / order tracking lists.</summary>
public sealed record CommercialDocumentSummary(
    long DocId,
    string DocKind,
    string DocNo,
    DateOnly DocDate,
    string? PartyCode,
    string? PartyName,
    decimal GrandTotal,
    string Status,
    DateOnly? DeliveryDate,
    DateOnly? ValidTill);

/// <summary>Per-material fulfilment/billing progress of a posted order.</summary>
public sealed record OrderProgressLine(
    long? MaterialId,
    string? MaterialCode,
    string Description,
    decimal OrderedQty,
    decimal FulfilledQty,
    decimal BilledQty,
    int LineNo = 0)
{
    public decimal RemainingToFulfill => Math.Max(0m, OrderedQty - FulfilledQty);
    public decimal RemainingToBill => Math.Max(0m, OrderedQty - BilledQty);
}

/// <summary>Order progress roll-up (ERPNext per_delivered / per_billed pattern).</summary>
public sealed record OrderProgress(
    long DocId,
    string DocKind,
    string DocNo,
    IReadOnlyList<OrderProgressLine> Lines,
    decimal PerFulfilled,
    decimal PerBilled,
    string Status = "")
{
    /// <summary>Derived chain status (sales orders); the raw status for every other document.</summary>
    public string DerivedStatus => SalesChainDerivedStatus.Derive(DocKind, Status, PerFulfilled, PerBilled);
}

/// <summary>
/// One delivery's contribution to a consolidated sales invoice (W5):
/// the posted delivery note to draw from and, optionally, the quantity
/// to bill per SOURCE line number (null = the full remaining quantity
/// of every line). Lines not listed are not billed.
/// </summary>
public sealed record ConsolidatedDeliverySelection(
    long DeliveryDocId,
    IReadOnlyDictionary<int, decimal>? QuantityBySourceLine = null);

/// <summary>
/// Billing state of one posted delivery note of a sales order (W5):
/// what was delivered in total and what posted invoices have already
/// billed against it. Feeds the consolidated-invoice picker.
/// </summary>
public sealed record OrderDeliveryBillingInfo(
    long DeliveryDocId,
    string DocNo,
    DateOnly DocDate,
    string Status,
    decimal DeliveredQty,
    decimal BilledQty)
{
    public decimal RemainingQty => Math.Max(0m, DeliveredQty - BilledQty);
}

/// <summary>Statuses stored in sales_picking.status (sales cycle W4, spec §4).</summary>
public static class SalesPickingStatus
{
    public const string Draft = "Draft";
    public const string Confirmed = "Confirmed";
    public const string Cancelled = "Cancelled";
}

/// <summary>One picked line of a sales picking: the order line identity
/// (material + source line number), what was ordered, and what the
/// warehouse actually picked.</summary>
public sealed record SalesPickingLineSnapshot(
    long PickingLineId,
    int SourceLineNo,
    long? MaterialId,
    string? MaterialCode,
    string Description,
    string? Unit,
    decimal OrderedQty,
    decimal PickedQty,
    long? WarehouseId);

/// <summary>A warehouse picking staged from a posted sales order. A
/// confirmed picking feeds delivery-note creation with its picked
/// quantities; it posts nothing by itself.</summary>
public sealed record SalesPickingSnapshot(
    long PickingId,
    Guid CompanyId,
    long SalesOrderId,
    string Status,
    string? CreatedBy,
    string? CreatedAt,
    string? ConfirmedBy,
    string? ConfirmedAt,
    IReadOnlyList<SalesPickingLineSnapshot> Lines);

/// <summary>One quoted item, normalized to the company's base currency for comparison.</summary>
public sealed record SupplierQuotationLineOffer(
    int RfqLineNo,
    long? MaterialId,
    string? MaterialCode,
    string Description,
    string? Unit,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPct,
    decimal TaxRate,
    decimal LineTotal,
    decimal BaseCurrencyLineTotal);

/// <summary>Comparable supplier offer linked to one sourcing request.</summary>
public sealed record SupplierQuotationOffer(
    long DocId,
    string DocNo,
    string SupplierName,
    string CurrencyCode,
    decimal ExchangeRate,
    decimal Subtotal,
    decimal TaxAmount,
    decimal GrandTotal,
    DateOnly QuotationDate,
    DateOnly? ValidTill,
    DateOnly? DeliveryDate,
    string Status,
    IReadOnlyList<SupplierQuotationLineOffer> Lines);
