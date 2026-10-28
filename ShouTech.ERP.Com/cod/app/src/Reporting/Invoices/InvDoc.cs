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
    Cancelled = 2
}

/// <summary>
/// Payment method on commercial invoices (Phoenix-style behaviour).
/// </summary>
public enum InvPaymentMethod
{
    /// <summary>نقداً — party account defaults to cash box.</summary>
    Cash = 0,
    /// <summary>آجل — party account cleared until party is chosen, then follows party AR/AP account.</summary>
    Credit = 1,
    /// <summary>شيكات — party account defaults to bank; still editable.</summary>
    Checks = 2
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
    public decimal TaxRate { get; set; }
    public int? WarehouseId { get; set; }
    public string WarehouseName { get; set; } = string.Empty;

    public decimal LineSubtotal
    {
        get
        {
            var gross = Qty * UnitPrice;
            var disc = gross * (DiscountPct / 100m);
            return decimal.Round(gross - disc, 4, MidpointRounding.AwayFromZero);
        }
    }

    public decimal LineTax
        => decimal.Round(LineSubtotal * (TaxRate / 100m), 4, MidpointRounding.AwayFromZero);

    public decimal LineTotal => LineSubtotal + LineTax;
}

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
    public string CurrencyCode { get; set; } = "SAR";
    public decimal ExchangeRate { get; set; } = 1m;
    public string? TaxCode { get; set; }
    public decimal DefaultTaxRate { get; set; }
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

    /// <summary>حساب العميل / المورد / الصندوق / البنك — editable, driven by payment method.</summary>
    public string PartyAccountName { get; set; } = string.Empty;
    public long? PartyAccountId { get; set; }

    /// <summary>حساب البضاعة / المبيعات / المشتريات / المرتجعات — default by document kind.</summary>
    public string GoodsAccountName { get; set; } = string.Empty;
    public long? GoodsAccountId { get; set; }

    /// <summary>
    /// When this document was opened/created from another invoice (return, copy, drill-down),
    /// these point at the original. Empty for a primary invoice opened directly.
    /// Phenix «الأصل» is enabled only when OriginalDocId is set.
    /// </summary>
    public long? OriginalDocId { get; set; }
    public InvDocKind? OriginalDocKind { get; set; }
    public string OriginalDocNo { get; set; } = string.Empty;

    /// <summary>
    /// Journal voucher number created when the invoice is posted.
    /// Phenix «السند» opens this voucher after posting.
    /// </summary>
    public string JournalDocNo { get; set; } = string.Empty;

    // ---- Phenix-style footer amounts ----
    /// <summary>حسم الأقلام (absolute amount deducted from lines subtotal).</summary>
    public decimal LinesDiscountAmount { get; set; }
    /// <summary>إضافي الأقلام (absolute amount added).</summary>
    public decimal LinesExtraAmount { get; set; }
    /// <summary>قيمة الدفعة / المدفوع on this document.</summary>
    public decimal PaidAmount { get; set; }

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

    /// <summary>مجموع الأقلام — lines subtotal before header discount/extra.</summary>
    public decimal LinesTotal => Subtotal;

    /// <summary>قيمة الفاتورة = مجموع الأقلام − حسم + إضافي + ضريبة.</summary>
    public decimal InvoiceValue =>
        decimal.Round(Subtotal - LinesDiscountAmount + LinesExtraAmount + TaxAmount, 4, MidpointRounding.AwayFromZero);

    /// <summary>الصافي بعد الحسم والإضافي (بدون فصل الضريبة إن دُمجت في قيمة الفاتورة).</summary>
    public decimal NetAmount =>
        decimal.Round(Subtotal - LinesDiscountAmount + LinesExtraAmount, 4, MidpointRounding.AwayFromZero);

    /// <summary>الباقي = قيمة الفاتورة − المدفوع.</summary>
    public decimal RemainingAmount =>
        decimal.Round(InvoiceValue - PaidAmount, 4, MidpointRounding.AwayFromZero);

    /// <summary>Legacy grand total alias.</summary>
    public decimal GrandTotal => InvoiceValue;

    /// <summary>
    /// Default goods / stock account caption by document kind
    /// (حساب المشتريات، مرتجع المشتريات، المبيعات، …).
    /// </summary>
    public static string DefaultGoodsAccountName(InvDocKind kind) => kind switch
    {
        InvDocKind.PurchaseInvoice => "حساب المشتريات",
        InvDocKind.PurchaseReturn => "حساب مرتجع المشتريات",
        InvDocKind.SalesInvoice => "حساب المبيعات",
        InvDocKind.SalesReturn => "حساب مرتجع المبيعات",
        InvDocKind.PosSale => "حساب مبيعات نقاط البيع",
        InvDocKind.Quotation => "حساب المبيعات",
        InvDocKind.StockIn => "حساب المخزون",
        InvDocKind.StockOut => "حساب المخزون",
        InvDocKind.StockTransfer => "حساب المخزون",
        InvDocKind.OpeningStock => "حساب بضاعة أول المدة",
        _ => "حساب البضاعة"
    };

    public static string DefaultCashAccountName() => "حساب الصندوق";
    public static string DefaultBankAccountName() => "حساب البنك";
    public static string DefaultWarehouseName() => "المستودع العام";

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
