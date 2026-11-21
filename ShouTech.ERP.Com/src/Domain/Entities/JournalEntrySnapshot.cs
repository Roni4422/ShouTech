namespace ShouTech.Domain.Entities;

/// <summary>
/// Immutable database read model for a saved journal. It carries display values needed
/// to reopen a journal without trusting values left in the window's in-memory grid.
/// </summary>
public sealed record JournalEntrySnapshot(
    Guid Id,
    Guid CompanyId,
    DateOnly EntryDate,
    string Description,
    string? DocumentNumber,
    string? ReferenceNumber,
    string CurrencyCode,
    decimal ExchangeRate,
    JournalEntryStatus Status,
    IReadOnlyList<JournalEntryLineSnapshot> Lines);

public sealed record JournalEntryLineSnapshot(
    int LineNumber,
    Guid AccountId,
    string AccountCode,
    string AccountName,
    decimal Debit,
    decimal Credit,
    string? CostCenter,
    string? Note,
    // Step 7 (migration 062): base-currency counterparts of Debit/Credit
    // at the line's effective exchange rate. Defaulted so pre-062 read
    // paths and test doubles keep constructing snapshots unchanged.
    decimal BaseDebit = 0m,
    decimal BaseCredit = 0m,
    decimal? ExchangeRate = null);
