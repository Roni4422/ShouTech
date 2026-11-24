namespace ShouTech.Domain.Entities;

public sealed class JournalEntry
{
    private readonly List<JournalLine> _lines = new();

    public Guid Id { get; } = Guid.NewGuid();
    public DateOnly EntryDate { get; }
    public string Description { get; }
    public string? DocumentNumber { get; }
    public string? ReferenceNumber { get; }
    public string CurrencyCode { get; }
    public decimal ExchangeRate { get; }
    public JournalEntryStatus Status { get; private set; } = JournalEntryStatus.Draft;
    public IReadOnlyCollection<JournalLine> Lines => _lines.AsReadOnly();

    public JournalEntry(DateOnly entryDate, string description, string? referenceNumber = null, string currencyCode = "SAR", decimal exchangeRate = 1m, string? documentNumber = null)
    {
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("Description is required.", nameof(description));
        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Trim().Length is < 3 or > 3)
            throw new ArgumentException("Currency code must be a three-letter ISO code.", nameof(currencyCode));
        if (exchangeRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(exchangeRate), "Exchange rate must be greater than zero.");

        EntryDate = entryDate;
        Description = description.Trim();
        DocumentNumber = string.IsNullOrWhiteSpace(documentNumber) ? null : documentNumber.Trim();
        ReferenceNumber = string.IsNullOrWhiteSpace(referenceNumber) ? null : referenceNumber.Trim();
        CurrencyCode = currencyCode.Trim().ToUpperInvariant();
        ExchangeRate = exchangeRate;
    }

    public void AddLine(Guid accountId, decimal debit, decimal credit, string? note = null, string? costCenter = null)
    {
        var line = new JournalLine(accountId, debit, credit, note, costCenter);
        _lines.Add(line);
    }

    public void EnsureBalanced()
    {
        if (_lines.Count < 2)
            throw new AppError(ErrorCodes.JournalTooFewLines, "A journal entry requires at least two lines.");

        var totalDebit = _lines.Sum(line => line.Debit);
        var totalCredit = _lines.Sum(line => line.Credit);

        if (totalDebit != totalCredit)
            throw new AppError(ErrorCodes.JournalNotBalanced, "Journal entry debit and credit totals must balance.");

        if (totalDebit == 0)
            throw new AppError(ErrorCodes.JournalZeroAmount, "A journal entry must have a non-zero amount.");
    }

    public void Post()
    {
        if (Status != JournalEntryStatus.Draft)
            throw new InvalidOperationException("Only draft journal entries can be posted.");

        EnsureBalanced();
        Status = JournalEntryStatus.Posted;
    }

    public void Reverse()
    {
        if (Status != JournalEntryStatus.Posted)
            throw new InvalidOperationException("Only posted journal entries can be reversed.");

        Status = JournalEntryStatus.Reversed;
    }
}

public enum JournalEntryStatus
{
    Draft,
    Posted,
    Reversed
}

/// <summary>
/// The resolved party a cash voucher settles (sales-cycle G23,
/// migration 061 <c>voucher_party_links</c>). Carried INTO
/// <see cref="ShouTech.Domain.Interfaces.IJournalRepository.SaveDraftAsync"/>
/// so the link row is written inside the journal draft transaction —
/// a draft voucher must never exist without its intended party link.
/// CompanyCode is the company CODE (the key voucher_party_links and
/// party_ledger carry); DocKind is RCV (customer) or PAY (supplier).
/// </summary>
public sealed record VoucherPartyLinkRequest(
    string CompanyCode,
    string PartyKind,
    long PartyId,
    string PartyCode,
    string DocKind,
    string DocNo,
    decimal Amount,
    string CurrencyCode);

public sealed class JournalLine
{
    public Guid AccountId { get; }
    public decimal Debit { get; }
    public decimal Credit { get; }
    public string? Note { get; }
    public string? CostCenter { get; }

    public JournalLine(Guid accountId, decimal debit, decimal credit, string? note = null, string? costCenter = null)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("Account is required.", nameof(accountId));
        if (debit < 0 || credit < 0)
            throw new ArgumentOutOfRangeException(nameof(debit), "Amounts cannot be negative.");
        if (debit > 0 && credit > 0)
            throw new ArgumentException("A line cannot contain both debit and credit.");
        if (debit == 0 && credit == 0)
            throw new ArgumentException("A line must contain a debit or credit amount.");

        AccountId = accountId;
        Debit = debit;
        Credit = credit;
        Note = note?.Trim();
        CostCenter = string.IsNullOrWhiteSpace(costCenter) ? null : costCenter.Trim();
    }
}

// ---- merged from src/Domain/Entities/JournalEntrySnapshot.cs ----
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

// ---- merged from src/Domain/Entities/GlPosting.cs ----
/// <summary>One GL line requested by a document posting, by account CODE.
/// The code is resolved against the canonical chart inside the posting
/// transaction; an unresolvable code fails the whole posting (ACC-5011).</summary>
public sealed record GlLineRequest(
    string AccountCode, decimal Debit, decimal Credit,
    string? CostCenter = null, string? Note = null);

/// <summary>A document posting request: header data plus balanced lines.
/// SourceDocType / SourceDocId / SourceDocNo link the created journal
/// entry back to its source document (migration 060).</summary>
public sealed record GlDocumentRequest(
    Guid CompanyId,
    DateOnly EntryDate,
    string Description,
    string SourceDocType,
    string SourceDocId,
    string SourceDocNo,
    string CurrencyCode,
    decimal ExchangeRate,
    IReadOnlyList<GlLineRequest> Lines);

/// <summary>Result of a document GL posting: the real journal entry.</summary>
public sealed record GlPostingResult(Guid JournalEntryId, string DocumentNumber)
{
    /// <summary>Budget warnings produced by BudgetGuard in Warn mode
    /// (Phase B 055); empty when no Active budget warned. A Stop-mode
    /// budget never reaches a result — it throws ACC-5018/5019.</summary>
    public IReadOnlyList<BudgetWarning> BudgetWarnings { get; init; } = Array.Empty<BudgetWarning>();
}

/// <summary>An authorized credit override for one posting (W1, migration
/// 095): the actor who holds SALES.CREDIT.OVERRIDE and the document
/// identity the credit_overrides audit row is recorded against. Lives in
/// Domain so repository contracts can carry it; PartyCreditGuard (Infra)
/// consumes it.</summary>
public sealed record CreditOverrideContext(string Actor, string? DocKind, string? DocNo);
