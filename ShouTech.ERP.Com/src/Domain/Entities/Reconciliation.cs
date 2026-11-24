namespace ShouTech.Domain.Entities;

/// <summary>
/// One open (posted, not fully settled) sales or purchase document.
/// OpenAmount is what can still be allocated: total minus paid_amount.
/// paid_amount is the single source of truth for settlement: the allocation
/// transaction increases it, and Allocated merely reports how much of it came
/// through payment allocations (informational, never subtracted twice).
/// </summary>
public sealed record OpenDocumentRow(
    long DocId,
    string DocTable,
    string DocNo,
    DateOnly DocDate,
    long PartyId,
    string? PartyName,
    string CurrencyCode,
    decimal Total,
    decimal Paid,
    decimal Allocated,
    DateOnly? DueDate = null,
    // Withholding tax (W1, migration 099): the part of a purchase
    // invoice's total that was withheld at posting. It is owed to the
    // tax authority, never to the supplier, so it is NOT allocatable:
    // paying (Total − Withholding − Paid) settles the invoice fully.
    decimal Withholding = 0m)
{
    public decimal OpenAmount => Total - Withholding - Paid;

    /// <summary>FIFO ordering basis for allocation suggestions (W8):
    /// the due date when the document carries one (sales documents,
    /// migration 095), else the document date.</summary>
    public DateOnly FifoDate => DueDate ?? DocDate;
}

/// <summary>
/// One party-ledger payment entry (credit side for customers, debit side for
/// suppliers) with the part already consumed by allocations.
/// </summary>
public sealed record UnallocatedPaymentRow(
    long LedgerId,
    DateOnly EntryDate,
    string? DocKind,
    string? DocNo,
    long PartyId,
    string? Note,
    decimal Amount,
    decimal Allocated,
    string? PartyName = null,
    string CurrencyCode = "SAR",
    // Supplier advances (migration 095): an 'ADV' ledger entry is an
    // advance against one PO; PoDocId/PoDocNo name that PO and the row
    // may only be allocated to invoices of that PO.
    bool IsAdvance = false,
    long? PoDocId = null,
    string? PoDocNo = null)
{
    public decimal Available => Amount - Allocated;
}
