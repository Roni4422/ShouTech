using System;
using System.Collections.Generic;
using System.Linq;

namespace ShouTech.App.Reporting.Invoices;

/// <summary>Commercial document kinds used across sales and purchases.</summary>
public enum InvDocKind
{
    SalesInvoice = 0,
    SalesReturn = 1,
    PosSale = 2,
    PurchaseInvoice = 10,
    PurchaseReturn = 11,
    StockIn = 20,
    StockOut = 21,
    StockTransfer = 22,
    OpeningStock = 23,
    InventoryCount = 24,
    Quotation = 30
}

/// <summary>Lifecycle of a commercial document.</summary>
public enum InvDocStatus
{
    Draft = 0,
    Posted = 1,
    Cancelled = 2,
    // A dedicated stock engine has claimed the document but has not yet
    // finalized its posting state. This must never be treated as editable Draft.
    Posting = 3
}

/// <summary>
/// Payment method on commercial invoices (Phoenix-style behaviour).
/// </summary>
public enum InvPaymentMethod
{
    /// <summary>Cash - party account defaults to cash box.</summary>
    Cash = 0,
    /// <summary>Credit - party account cleared until party is chosen, then follows party AR/AP account.</summary>
    Credit = 1,
    /// <summary>Checks - party account defaults to bank; still editable.</summary>
    Checks = 2,
    /// <summary>Card - bank card networks (K-Net, Visa, ...); settles through the bank.</summary>
    Card = 3
}

/// <summary>One line on a commercial invoice / return.</summary>
public sealed class InvLine
{
    public int LineNo { get; set; }
    public long? MaterialId { get; set; }
    public string MaterialCode { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public decimal Qty { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPct { get; set; }
    public string TaxCode { get; set; } = string.Empty;
    public string TaxCalculationMode { get; set; } = "Exclusive";
    public decimal TaxRate { get; set; }
    public int? WarehouseId { get; set; }
    public string WarehouseName { get; set; } = string.Empty;

    // ---- Sales-return routing (W6, migration 100) ----------------------
    /// <summary>Condition the returned goods are in: Good (back to
    /// available stock), Quarantine (into the quarantine warehouse) or
    /// Damaged (never returns to stock; no cost reversal posts).
    /// Meaningful on SalesReturn lines only; persisted in
    /// sales_document_lines.return_condition.</summary>
    public string ReturnCondition { get; set; } = "Good";
    /// <summary>Optional per-line return-reason override (a code from
    /// srt_return_reasons); null/empty = the header reason applies.
    /// Persisted in sales_document_lines.return_reason_code.</summary>
    public string? ReturnReasonCode { get; set; }

    // ---- Batch/serial capture (Phase B 054) ---------------------------
    // Captured at posting time by the batch/serial picker (quantities in
    // the line's chosen unit for batches — StkPost restates them per
    // base unit exactly like the line quantity; serials are per unit and
    // unit-independent). Transient by design: the persisted record of a
    // posting is stock_movement_allocations, written by the engine.
    public List<ShouTech.Infrastructure.BatchAllocationRequest> BatchAllocations { get; set; } = new();
    public List<ShouTech.Infrastructure.SerialSelection> Serials { get; set; } = new();
    /// <summary>Holder of the INV.BATCH.EXPIREDOVERRIDE permission
    /// confirmed the expired-batch override for this line at capture.</summary>
    public bool AllowExpiredBatchIssue { get; set; }

    private decimal DiscountedEnteredAmount
    {
        get
        {
            var gross = Qty * UnitPrice;
            var discount = gross * (DiscountPct / 100m);
            return decimal.Round(gross - discount, 4, MidpointRounding.AwayFromZero);
        }
    }

    public decimal LineSubtotal
    {
        get
        {
            var enteredAmount = DiscountedEnteredAmount;
            if (!string.Equals(TaxCalculationMode, "Inclusive", StringComparison.OrdinalIgnoreCase) || TaxRate <= 0m)
                return enteredAmount;

            var divisor = 1m + (TaxRate / 100m);
            return decimal.Round(enteredAmount / divisor, 4, MidpointRounding.AwayFromZero);
        }
    }

    public decimal LineTax
    {
        get
        {
            var enteredAmount = DiscountedEnteredAmount;
            if (TaxRate <= 0m) return 0m;
            if (string.Equals(TaxCalculationMode, "Inclusive", StringComparison.OrdinalIgnoreCase))
                return decimal.Round(enteredAmount - LineSubtotal, 4, MidpointRounding.AwayFromZero);
            return decimal.Round(LineSubtotal * (TaxRate / 100m), 4, MidpointRounding.AwayFromZero);
        }
    }

    public decimal LineTotal => LineSubtotal + LineTax;
}

/// <summary>Batch/serial capture for one document line, keyed by line
/// number into <see cref="InvRepo"/> posting: what the picker collected
/// at posting time (Phase B 054).</summary>
public sealed record LineBatchSerialCapture(
    List<ShouTech.Infrastructure.BatchAllocationRequest> Batches,
    List<ShouTech.Infrastructure.SerialSelection> Serials,
    bool AllowExpiredBatchIssue);

/// <summary>
/// Full commercial document model (sales or purchase).
/// Header accounts, payment method, and footer amounts follow Phoenix-style invoice windows.
/// </summary>
public sealed class InvDoc
{
    public long DocId { get; set; }
    public InvDocKind Kind { get; set; } = InvDocKind.SalesInvoice;
    public string DocNo { get; set; } = string.Empty;
    public DateTime DocDate { get; set; } = DateTime.Today;
    public long? PartyId { get; set; }
    public string PartyCode { get; set; } = string.Empty;
    public string PartyName { get; set; } = string.Empty;
    public int? WarehouseId { get; set; }
    public string WarehouseName { get; set; } = string.Empty;
    public int? TargetWarehouseId { get; set; }
    public string TargetWarehouseName { get; set; } = string.Empty;
    public string CurrencyCode { get; set; } = "SAR";
    public decimal ExchangeRate { get; set; } = 1m;
    public string? TaxCode { get; set; }
    public decimal DefaultTaxRate { get; set; }
    /// <summary>Quotation validity deadline (ERPNext "Valid Till",
    /// sales-cycle G4): persisted in sales_documents.valid_till
    /// (migration 032) by InvRepo for every native document kind, but
    /// only meaningful — and only shown in InvoiceWin — for
    /// quotations. Null = no expiry set.</summary>
    public DateTime? ValidTill { get; set; }
    public string? Notes { get; set; }
    public InvDocStatus Status { get; set; } = InvDocStatus.Draft;
    public List<InvLine> Lines { get; set; } = new();

    // ---- Phoenix-style header ----
    public InvPaymentMethod PaymentMethod { get; set; } = InvPaymentMethod.Cash;
    public string ReceiptNo { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public string SalesRepName { get; set; } = string.Empty;
    public string CostCenter { get; set; } = string.Empty;

    /// <summary>Customer / supplier / cash / bank account - editable, driven by payment method.</summary>
    public string PartyAccountName { get; set; } = string.Empty;
    public long? PartyAccountId { get; set; }

    /// <summary>Goods / sales / purchases / returns account - default by document kind.</summary>
    public string GoodsAccountName { get; set; } = string.Empty;
    public long? GoodsAccountId { get; set; }

    /// <summary>
    /// When this document was opened/created from another invoice (return, copy, drill-down),
    /// these point at the original. Empty for a primary invoice opened directly.
    /// Phenix "original" is enabled only when OriginalDocId is set.
    /// </summary>
    public long? OriginalDocId { get; set; }
    public InvDocKind? OriginalDocKind { get; set; }
    public string OriginalDocNo { get; set; } = string.Empty;

    // ---- Sales-return completion (W6, migration 100) -------------------
    /// <summary>Header return-reason code (srt_return_reasons.code).
    /// Required on every sales return created through the supported
    /// paths (SalesReturnWin picker, SrtRepo, POS returns and generated
    /// reversals stamp OTHER). Persisted in
    /// sales_documents.return_reason_code.</summary>
    public string ReturnReasonCode { get; set; } = string.Empty;
    /// <summary>Source shape of a sales return: "INV" (invoice / POS
    /// sale), "DLV" (posted delivery with no invoice yet — OriginalDocId
    /// then points at the delivery, whose doc_kind is 'DLV' and which
    /// has no InvDocKind), or "NONE" (no-invoice walk-in return).
    /// Empty on legacy / non-return documents; persisted in
    /// sales_documents.return_source_kind.</summary>
    public string ReturnSourceKind { get; set; } = string.Empty;
    /// <summary>True for an authorized no-invoice (walk-in) return:
    /// no source document, credit goes to the party ledger only.
    /// Persisted in sales_documents.no_invoice.</summary>
    public bool NoInvoice { get; set; }
    /// <summary>Inspection workflow state (migration 100):
    /// NotRequired / PendingInspection / Inspected.</summary>
    public string InspectionStatus { get; set; } = "NotRequired";
    /// <summary>Approval workflow state (migration 100):
    /// NotRequired / PendingApproval / Approved / Rejected.</summary>
    public string ApprovalStatus { get; set; } = "NotRequired";

    // ---- Export invoice fields (W5, migration 102) ------------------
    /// <summary>True when this sales invoice is an EXPORT invoice; the
    /// shipment/customs fields below are then shown and edited on
    /// InvoiceWin. Persisted in sales_documents.is_export.</summary>
    public bool IsExport { get; set; }
    /// <summary>Incoterm code (EXW / FOB / CIF ...). sales_documents.incoterm.</summary>
    public string Incoterm { get; set; } = string.Empty;
    /// <summary>Port of loading. sales_documents.port_of_loading.</summary>
    public string PortOfLoading { get; set; } = string.Empty;
    /// <summary>Port of discharge. sales_documents.port_of_discharge.</summary>
    public string PortOfDischarge { get; set; } = string.Empty;
    /// <summary>Country of origin. sales_documents.country_of_origin.</summary>
    public string CountryOfOrigin { get; set; } = string.Empty;
    /// <summary>Customs declaration reference. sales_documents.customs_ref.</summary>
    public string CustomsRef { get; set; } = string.Empty;
    /// <summary>Vessel name / flight number. sales_documents.vessel_flight.</summary>
    public string VesselFlight { get; set; } = string.Empty;
    /// <summary>Gross weight (document unit of weight, as entered).
    /// sales_documents.gross_weight; null = not recorded.</summary>
    public decimal? GrossWeight { get; set; }
    /// <summary>Net weight. sales_documents.net_weight; null = not recorded.</summary>
    public decimal? NetWeight { get; set; }

    /// <summary>
    /// Journal voucher number created when the invoice is posted.
    /// Phenix "voucher" opens this voucher after posting.
    /// </summary>
    public string JournalDocNo { get; set; } = string.Empty;

    // ---- Phenix-style footer amounts ----
    /// <summary>Lines discount (absolute amount deducted from lines subtotal).</summary>
    public decimal LinesDiscountAmount { get; set; }
    /// <summary>Lines extra (absolute amount added).</summary>
    public decimal LinesExtraAmount { get; set; }
    /// <summary>Payment value / paid amount on this document.</summary>
    public decimal PaidAmount { get; set; }

    // ---- POS split tenders (Build POS 066, transient) ------------------
    // When a POS sale / POS return is settled by MORE THAN ONE tender,
    // the single PaymentMethod cannot express the GL settlement legs.
    // PosPosting fills these two magnitudes (cash part / card part of
    // PaidAmount; the remainder of the total is the receivable leg) and
    // InvRepo.BuildGlLines posts Cash 131 / Bank 132 accordingly. Null
    // on every non-POS document and on single-tender POS documents,
    // where the legacy PaymentMethod leg is already exact.
    public decimal? PosCashApplied { get; set; }
    public decimal? PosCardApplied { get; set; }

    public string KindCode => Kind switch
    {
        InvDocKind.SalesInvoice => "SAL",
        InvDocKind.SalesReturn => "SRT",
        InvDocKind.PosSale => "POS",
        InvDocKind.PurchaseInvoice => "PUR",
        InvDocKind.PurchaseReturn => "PRT",
        InvDocKind.StockIn => "ENT",
        InvDocKind.StockOut => "EXT",
        InvDocKind.StockTransfer => "TRN",
        InvDocKind.OpeningStock => "OPN",
        InvDocKind.InventoryCount => "CNT",
        InvDocKind.Quotation => "QUO",
        _ => "DOC"
    };

    public bool IsSalesSide =>
        Kind is InvDocKind.SalesInvoice or InvDocKind.SalesReturn or InvDocKind.PosSale or InvDocKind.Quotation;

    public decimal Subtotal => Lines.Sum(l => l.LineSubtotal);
    public decimal TaxAmount => Lines.Sum(l => l.LineTax);

    /// <summary>Lines total - lines subtotal before header discount/extra.</summary>
    public decimal LinesTotal => Subtotal;

    /// <summary>Invoice value = lines total - discount + extra + tax.</summary>
    public decimal InvoiceValue =>
        decimal.Round(Subtotal - LinesDiscountAmount + LinesExtraAmount + TaxAmount, 4, MidpointRounding.AwayFromZero);

    /// <summary>Net after discount and extra (without separating tax if it is merged into the invoice value).</summary>
    public decimal NetAmount =>
        decimal.Round(Subtotal - LinesDiscountAmount + LinesExtraAmount, 4, MidpointRounding.AwayFromZero);

    /// <summary>Remaining = invoice value - paid.</summary>
    public decimal RemainingAmount =>
        decimal.Round(InvoiceValue - PaidAmount, 4, MidpointRounding.AwayFromZero);

    /// <summary>Legacy grand total alias (recomputed from the in-memory lines).</summary>
    public decimal GrandTotal => InvoiceValue;

    // ---- Stored totals (Fix Phase Step 5) ----
    // The header row persisted by SaveDraft carries subtotal / tax_amount /
    // grand_total computed AT SAVE TIME from the lines + header discount /
    // extra. When a document is reloaded for POSTING, those stored figures
    // are the amounts of record: posting (GL, party_ledger, credit guard)
    // must use them exactly, never a recomputation that can drop the
    // header discount/extra (sweep I-1: a document storing 1,050 was
    // debited 1,150). Null in memory-built documents (drafts not yet
    // saved), where the recomputed value is the only one that exists.
    public decimal? StoredSubtotal { get; set; }
    public decimal? StoredTaxAmount { get; set; }
    public decimal? StoredGrandTotal { get; set; }

    /// <summary>Subtotal that posting must use: the stored figure when present.</summary>
    public decimal PostingSubtotal => StoredSubtotal ?? Subtotal;

    /// <summary>Tax amount that posting must use: the stored figure when present.</summary>
    public decimal PostingTaxAmount => StoredTaxAmount ?? TaxAmount;

    /// <summary>
    /// THE amount posting must use everywhere (GL control legs,
    /// party_ledger, credit guard, settlement cap): the stored
    /// grand_total when the document was loaded from storage; the
    /// recomputed invoice value only for a never-saved in-memory draft.
    /// </summary>
    public decimal PostingTotal => StoredGrandTotal ?? GrandTotal;

    /// <summary>
    /// Default goods / stock account caption by document kind
    /// (purchases account, purchase returns, sales, ...).
    /// </summary>
    public static string DefaultGoodsAccountName(InvDocKind kind) => kind switch
    {
        InvDocKind.PurchaseInvoice => "Purchases Account",
        InvDocKind.PurchaseReturn => "Purchase Returns Account",
        InvDocKind.SalesInvoice => "Sales Account",
        InvDocKind.SalesReturn => "Sales Returns Account",
        InvDocKind.PosSale => "POS Sales Account",
        InvDocKind.Quotation => "Sales Account",
        InvDocKind.StockIn => "Inventory Account",
        InvDocKind.StockOut => "Inventory Account",
        InvDocKind.StockTransfer => "Inventory Account",
        InvDocKind.OpeningStock => "Opening Inventory Account",
        _ => "Goods Account"
    };

    public static string DefaultCashAccountName() => "Cash Account";
    public static string DefaultBankAccountName() => "Bank Account";
    public static string DefaultWarehouseName() => "Main Warehouse";

    public void RenumberLines()
    {
        for (var i = 0; i < Lines.Count; i++)
            Lines[i].LineNo = i + 1;
    }

    public void RecalcLineTaxesFromDefault()
    {
        foreach (var line in Lines)
        {
            if (line.TaxRate == 0 && DefaultTaxRate > 0)
                line.TaxRate = DefaultTaxRate;
        }
    }

    /// <summary>Apply payment-method rules to the party account field.</summary>
    public void ApplyPaymentMethodDefaults(bool partyJustResolved = false)
    {
        switch (PaymentMethod)
        {
            case InvPaymentMethod.Cash:
                if (string.IsNullOrWhiteSpace(PartyAccountName) ||
                    PartyAccountName == DefaultBankAccountName() ||
                    partyJustResolved)
                    PartyAccountName = DefaultCashAccountName();
                break;
            case InvPaymentMethod.Credit:
                if (!partyJustResolved)
                    PartyAccountName = string.Empty;
                else if (!string.IsNullOrWhiteSpace(PartyName))
                    PartyAccountName = PartyName;
                break;
            case InvPaymentMethod.Checks:
                if (string.IsNullOrWhiteSpace(PartyAccountName) ||
                    PartyAccountName == DefaultCashAccountName() ||
                    partyJustResolved)
                    PartyAccountName = DefaultBankAccountName();
                break;
        }
    }
}
