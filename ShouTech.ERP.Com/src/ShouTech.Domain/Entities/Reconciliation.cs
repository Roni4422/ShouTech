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
    decimal Allocated)
{
    public decimal OpenAmount => Total - Paid;
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
    decimal Allocated)
{
    public decimal Available => Amount - Allocated;
}
