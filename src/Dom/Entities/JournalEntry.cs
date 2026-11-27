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
            throw new InvalidOperationException("A journal entry requires at least two lines.");

        var totalDebit = _lines.Sum(l => l.Debit);
        var totalCredit = _lines.Sum(l => l.Credit);
        if (totalDebit <= 0 || totalCredit <= 0)
            throw new InvalidOperationException("Journal totals must be greater than zero.");
        if (totalDebit != totalCredit)
            throw new InvalidOperationException("Journal entry is not balanced.");
    }

    public void MarkPosted() => Status = JournalEntryStatus.Posted;
    public void MarkReversed() => Status = JournalEntryStatus.Reversed;
}

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
            throw new ArgumentOutOfRangeException("Debit and credit cannot be negative.");
        if (debit > 0 && credit > 0)
            throw new ArgumentException("A line cannot have both debit and credit.");
        if (debit == 0 && credit == 0)
            throw new ArgumentException("A line must have a debit or a credit.");

        AccountId = accountId;
        Debit = debit;
        Credit = credit;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        CostCenter = string.IsNullOrWhiteSpace(costCenter) ? null : costCenter.Trim();
    }
}

public enum JournalEntryStatus
{
    Draft = 0,
    Posted = 1,
    Reversed = 2
}

public sealed record VoucherPartyLinkRequest(
    string CompanyCode,
    string PartyKind,
    long PartyId,
    string PartyCode,
    string DocKind,
    string DocNo,
    decimal Amount,
    string CurrencyCode);

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
    decimal BaseDebit = 0m,
    decimal BaseCredit = 0m,
    decimal ExchangeRate = 1m);

/// <summary>One GL line requested by a document posting, by account code.
/// The infrastructure posting engine resolves the code against the company's chart.</summary>
public sealed record GlLineRequest(
    string AccountCode, decimal Debit, decimal Credit,
    string? CostCenter = null, string? Note = null);

/// <summary>A document-to-GL posting request. Source identity links the
/// resulting journal entry to its originating business document.</summary>
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

/// <summary>Result of a document GL posting, including non-blocking budget warnings.</summary>
public sealed record GlPostingResult(Guid JournalEntryId, string DocumentNumber)
{
    public IReadOnlyList<BudgetWarning> BudgetWarnings { get; init; } = Array.Empty<BudgetWarning>();
}

public sealed record CreditOverrideContext(string Actor, string? DocKind, string? DocNo);
