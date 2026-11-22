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
    public const string PurchaseInvoice = "PUR";
}

/// <summary>Statuses stored in the commercial document status column (TEXT).</summary>
public static class CommercialDocumentStatus
{
    public const string Draft = "Draft";
    public const string Posted = "Posted";
    public const string Closed = "Closed";
    public const string Cancelled = "Cancelled";
    public const string Lost = "Lost";
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
    long? WarehouseId);

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
    DateOnly? ValidTill = null);

/// <summary>A cross-document chain link (see dba document_links table).</summary>
public sealed record CommercialDocumentLink(
    long LinkId,
    Guid CompanyId,
    string SourceDocKind,
    long SourceDocId,
    string TargetDocKind,
    long TargetDocId);

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
    int? SourceLineNo = null);

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
    string? WarehouseName = null);

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
    decimal BilledQty)
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
    decimal PerBilled);

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
    string Status);
