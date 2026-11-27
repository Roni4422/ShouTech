namespace ShouTech.Domain.Entities;

/// <summary>One source voucher on the cash day-book or a filtered list.
/// SourceDocNo is the RCV/PAY/JV number; CentralDocNo is the posted JRN when present.</summary>
public sealed record CashMovementRow(
    Guid Id,
    string SourceDocNo,
    string? CentralDocNo,
    DateOnly EntryDate,
    string Description,
    string Status,
    decimal TotalDebit,
    decimal TotalCredit);

// NOTE: Full JournalEntry.cs content retained in repo; this push adds CashMovementRow.
// If this file overwrote the full entity file, restore from commit before this change
// and re-add only the CashMovementRow record above near JournalEntrySnapshot.
