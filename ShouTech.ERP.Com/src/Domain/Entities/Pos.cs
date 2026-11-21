namespace ShouTech.Domain.Entities;

/// <summary>POS session lifecycle (migration 066).</summary>
public static class PosSessionStatuses
{
    public const string Open = "Open";
    public const string Closed = "Closed";
}

/// <summary>Tender types stored in pos_sale_tenders.tender_type. A sale
/// is settled by any combination of these three; the on-account part is
/// the portion NOT collected at the till, which the document posting
/// leaves on the party ledger (step-4 settlement covers the rest).</summary>
public static class PosTenderTypes
{
    public const string Cash = "Cash";
    public const string Card = "Card";
    public const string OnAccount = "OnAccount";
    public static readonly IReadOnlyList<string> All = new[] { Cash, Card, OnAccount };
}

/// <summary>Held-bill lifecycle (migration 066).</summary>
public static class PosHeldStatuses
{
    public const string Held = "Held";
    public const string Recalled = "Recalled";
    public const string Cancelled = "Cancelled";
}

/// <summary>One POS session: a cashier's shift on one terminal, from the
/// opening cash count to the counted close (migration 066). Expected /
/// variance are written at close from real session data:
/// expected = opening + SUM(Cash tenders of the session) — refund rows
/// are negative, so returns reduce the expectation automatically.</summary>
public sealed class PosSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SessionNo { get; set; } = string.Empty;
    public string TerminalCode { get; set; } = string.Empty;
    public string Cashier { get; set; } = string.Empty;
    public int? WarehouseId { get; set; }
    public string CurrencyCode { get; set; } = "SAR";
    public decimal OpeningCash { get; set; }
    public string Status { get; set; } = PosSessionStatuses.Open;
    public string? OpenedAt { get; set; }
    public string? OpenedBy { get; set; }
    public string? ClosedAt { get; set; }
    public string? ClosedBy { get; set; }
    public decimal? CountedCash { get; set; }
    public decimal? ExpectedCash { get; set; }
    public decimal? Variance { get; set; }
    public string? Notes { get; set; }
    public string DisplayName => $"{SessionNo} — {TerminalCode} ({Status})";
}

/// <summary>One tender applied to one POS document. Amount is the part
/// of the bill this tender settles (always positive on a sale; refund
/// rows written by a return carry a NEGATIVE amount). For cash,
/// TenderedAmount is what the customer handed over and ChangeAmount
/// the over-tender returned — change never settles the bill.</summary>
public sealed class PosTender
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? SessionId { get; set; }
    public long DocId { get; set; }
    public string DocNo { get; set; } = string.Empty;
    public string DocKind { get; set; } = "POS";
    public string TenderType { get; set; } = PosTenderTypes.Cash;
    public decimal Amount { get; set; }
    public decimal? TenderedAmount { get; set; }
    public decimal ChangeAmount { get; set; }
    public string? CardRef { get; set; }
}

/// <summary>One line of a parked bill (migration 066).</summary>
public sealed class PosHeldLine
{
    public int LineNo { get; set; }
    public long? MaterialId { get; set; }
    public string? MaterialCode { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? Unit { get; set; }
    public decimal Qty { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPct { get; set; }
    public decimal TaxRate { get; set; }
    public int? WarehouseId { get; set; }
    public decimal LineSubtotal => decimal.Round(Qty * UnitPrice * (1m - DiscountPct / 100m), 4, MidpointRounding.AwayFromZero);
    public decimal LineTax => decimal.Round(LineSubtotal * TaxRate / 100m, 4, MidpointRounding.AwayFromZero);
    public decimal LineTotal => LineSubtotal + LineTax;
}

/// <summary>A parked (held) bill: persisted in the database with its
/// lines, its customer and its bill discount, so it survives the till
/// closing and recalls with identical lines and totals.</summary>
public sealed class PosHeldBill
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? SessionId { get; set; }
    public string HeldNo { get; set; } = string.Empty;
    public string Cashier { get; set; } = string.Empty;
    public long? PartyId { get; set; }
    public string? PartyCode { get; set; }
    public string? PartyName { get; set; }
    public string CurrencyCode { get; set; } = "SAR";
    public decimal BillDiscount { get; set; }
    public string? Notes { get; set; }
    public string Status { get; set; } = PosHeldStatuses.Held;
    public string? CreatedAt { get; set; }
    public List<PosHeldLine> Lines { get; set; } = new();
    public decimal Subtotal => Lines.Sum(l => l.LineSubtotal);
    public decimal TaxAmount => Lines.Sum(l => l.LineTax);
    /// <summary>The bill total exactly as the till computes it: lines
    /// minus the bill discount plus tax (the InvDoc shape).</summary>
    public decimal GrandTotal => decimal.Round(Subtotal - BillDiscount + TaxAmount, 4, MidpointRounding.AwayFromZero);
}

/// <summary>Per-company POS settings (migration 066). Session-less
/// selling is possible ONLY when AllowSessionless is explicitly set —
/// and even then the caller must hold POS.SESSION.BYPASS.</summary>
public sealed class PosSettings
{
    public bool AllowSessionless { get; set; }
    public string? DefaultTerminalCode { get; set; }
    public int? DefaultWarehouseId { get; set; }
}

/// <summary>One product row for the POS touch grid / search: the
/// material's own data (code, names, sale price, default unit, tax,
/// image path where the material card carries one).</summary>
public sealed class PosProduct
{
    public long MaterialId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string Unit { get; set; } = string.Empty;
    public decimal TaxRate { get; set; }
    public string? ImagePath { get; set; }
    public bool HasBatch { get; set; }
    public bool HasSerial { get; set; }
    public decimal OnHandQty { get; set; }
    public decimal ReservedQty { get; set; }
    public decimal SellableQty => Math.Max(0m, OnHandQty - ReservedQty);
}

/// <summary>One line of the original bill as a POS return sees it: what
/// was sold, what has already been returned by earlier POS returns,
/// and what remains returnable.</summary>
public sealed record PosReturnableLine(
    int LineNo, long? MaterialId, string MaterialCode, string Description,
    string? Unit, decimal QtySold, decimal UnitPrice, decimal DiscountPct,
    decimal TaxRate, int? WarehouseId, decimal QtyAlreadyReturned)
{
    public decimal QtyReturnable => QtySold - QtyAlreadyReturned;
    public string TaxCalculationMode { get; init; } = "Exclusive";
}

/// <summary>The original posted POS bill a return is taken against.</summary>
public sealed class PosOriginalBill
{
    public long DocId { get; set; }
    public string DocNo { get; set; } = string.Empty;
    public string DocDate { get; set; } = string.Empty;
    public long? PartyId { get; set; }
    public string? PartyCode { get; set; }
    public string? PartyName { get; set; }
    public string CurrencyCode { get; set; } = "SAR";
    public decimal ExchangeRate { get; set; } = 1m;
    public decimal DiscountAmount { get; set; }
    public decimal ExtraAmount { get; set; }
    public decimal GrandTotal { get; set; }
    public decimal PaidAmount { get; set; }
    public Guid? SessionId { get; set; }
    public List<PosReturnableLine> Lines { get; set; } = new();
    /// <summary>Lines subtotal of the ORIGINAL bill (for pro-rata
    /// header discount/extra allocation to a partial return).</summary>
    public decimal LinesSubtotal => Lines.Sum(l => decimal.Round(
        l.QtySold * l.UnitPrice * (1m - l.DiscountPct / 100m), 4, MidpointRounding.AwayFromZero));
}

/// <summary>Z-report data for one session (real session rows only):
/// sales/returns counts and totals, tenders by type, tax, and the
/// drawer expectation against the counted close.</summary>
public sealed class PosZReport
{
    public Guid SessionId { get; set; }
    public string SessionNo { get; set; } = string.Empty;
    public string TerminalCode { get; set; } = string.Empty;
    public string Cashier { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal OpeningCash { get; set; }
    public int SalesCount { get; set; }
    public decimal SalesTotal { get; set; }
    public int ReturnsCount { get; set; }
    public decimal ReturnsTotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal CashTendered { get; set; }
    public decimal CardTendered { get; set; }
    public decimal OnAccountTendered { get; set; }
    public decimal CashRefunded { get; set; }
    public decimal CardRefunded { get; set; }
    /// <summary>opening + cash sales − cash refunds.</summary>
    public decimal ExpectedCash { get; set; }
    public decimal? CountedCash { get; set; }
    public decimal? Variance { get; set; }
    public decimal NetSales => SalesTotal - ReturnsTotal;
}

/// <summary>
/// Pure POS tender / drawer math — no I/O, so the Kern.Tests facts, the
/// repository and the windows can never disagree.
///
/// Sale rule: the APPLIED amounts (cash applied + card + on-account)
/// must equal the bill total exactly. Cash may be tendered above its
/// applied part; the excess is change and settles nothing. On-account
/// requires a named party (a walk-in has no ledger to owe on).
/// </summary>
public static class PosTenderMath
{
    public static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Validates a sale's tenders against its bill total and
    /// returns the cash change due. Throws AppError POS-1004/POS-1005.</summary>
    public static decimal ValidateSaleTenders(decimal billTotal, IReadOnlyList<PosTender> tenders, bool hasParty)
    {
        if (billTotal < 0) throw new AppError(ErrorCodes.PosTenderInvalid, "A bill total cannot be negative.");
        ArgumentNullException.ThrowIfNull(tenders);
        var applied = 0m;
        var change = 0m;
        foreach (var t in tenders)
        {
            if (!PosTenderTypes.All.Contains(t.TenderType))
                throw new AppError(ErrorCodes.PosTenderInvalid, $"Unknown tender type '{t.TenderType}'.", t.TenderType);
            if (t.Amount < 0)
                throw new AppError(ErrorCodes.PosTenderInvalid, "A sale tender amount cannot be negative.");
            applied += t.Amount;
            if (t.TenderType == PosTenderTypes.OnAccount && t.Amount > 0 && !hasParty)
                throw new AppError(ErrorCodes.PosTenderInvalid,
                    "An on-account tender requires a named customer; a walk-in sale cannot be put on account.");
            if (t.TenderType == PosTenderTypes.Cash)
            {
                var tendered = t.TenderedAmount ?? t.Amount;
                if (tendered < t.Amount)
                    throw new AppError(ErrorCodes.PosTenderInvalid,
                        "The cash handed over cannot be less than the cash amount applied to the bill.");
                change += tendered - t.Amount;
                if (t.ChangeAmount != 0m && Round(t.ChangeAmount) != Round(tendered - t.Amount))
                    throw new AppError(ErrorCodes.PosTenderInvalid,
                        "The recorded change does not equal the cash over-tender.");
            }
            if (t.TenderType == PosTenderTypes.Card && t.TenderedAmount is decimal cardTendered && cardTendered != t.Amount)
                throw new AppError(ErrorCodes.PosTenderInvalid,
                    "A card tender is authorised for its exact amount; it cannot over-tender.");
        }
        if (Round(applied) != Round(billTotal))
            throw new AppError(ErrorCodes.PosTenderMismatch,
                $"Tenders settle {Round(applied)} but the bill total is {Round(billTotal)}; the applied tenders must equal the bill exactly (over-tendered cash is change, not settlement).");
        return Round(change);
    }

    /// <summary>The amount of a sale that was actually collected at the
    /// till (cash applied + card) — the document's paid_amount, whose
    /// party-ledger settlement step 4 already posts. The on-account
    /// remainder stays as the party's balance.</summary>
    public static decimal CollectedAmount(IReadOnlyList<PosTender> tenders)
        => tenders.Where(t => t.TenderType is PosTenderTypes.Cash or PosTenderTypes.Card).Sum(t => t.Amount);

    public static decimal OnAccountAmount(IReadOnlyList<PosTender> tenders)
        => tenders.Where(t => t.TenderType == PosTenderTypes.OnAccount).Sum(t => t.Amount);

    public static decimal CashApplied(IReadOnlyList<PosTender> tenders)
        => tenders.Where(t => t.TenderType == PosTenderTypes.Cash).Sum(t => t.Amount);

    public static decimal CardApplied(IReadOnlyList<PosTender> tenders)
        => tenders.Where(t => t.TenderType == PosTenderTypes.Card).Sum(t => t.Amount);

    /// <summary>Expected drawer cash: opening float + cash applied on
    /// sales − cash refunded on returns (refund tender rows are stored
    /// negative, so this is opening + SUM(cash amounts)).</summary>
    public static decimal ExpectedCash(decimal openingCash, IEnumerable<PosTender> sessionTenders)
        => openingCash + sessionTenders.Where(t => t.TenderType == PosTenderTypes.Cash).Sum(t => t.Amount);

    /// <summary>Validates return quantities against the original bill:
    /// every returned line must exist on the original and the quantity
    /// (in the original line's unit, the only unit a return is taken
    /// in) must not exceed what remains returnable. POS-1006.</summary>
    public static void ValidateReturnQuantities(PosOriginalBill original, IReadOnlyDictionary<int, decimal> qtyByLineNo)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(qtyByLineNo);
        foreach (var (lineNo, qty) in qtyByLineNo)
        {
            if (qty <= 0) continue;
            var line = original.Lines.FirstOrDefault(l => l.LineNo == lineNo)
                ?? throw new AppError(ErrorCodes.PosReturnOriginalInvalid,
                    $"Line {lineNo} is not on the original bill '{original.DocNo}'.", original.DocNo);
            if (qty > line.QtyReturnable)
                throw new AppError(ErrorCodes.PosReturnExceedsSold,
                    $"Cannot return {qty} of '{line.Description}': {line.QtySold} sold, {line.QtyAlreadyReturned} already returned.", line.MaterialCode);
        }
    }

    /// <summary>Pro-rata share of an original header amount (discount /
    /// extra / paid) belonging to a partial return whose lines subtotal
    /// is <paramref name="returnedLinesSubtotal"/> of the original's
    /// <paramref name="originalLinesSubtotal"/> — the step-5 symmetric
    /// allocation, so original + return stored totals net exactly.</summary>
    public static decimal ProRata(decimal headerAmount, decimal returnedLinesSubtotal, decimal originalLinesSubtotal)
    {
        if (originalLinesSubtotal == 0m) return 0m;
        return decimal.Round(headerAmount * returnedLinesSubtotal / originalLinesSubtotal, 4, MidpointRounding.AwayFromZero);
    }
}
