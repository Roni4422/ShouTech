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

        var totalDebit = _lines.Sum(line => line.Debit);
        var totalCredit = _lines.Sum(line => line.Credit);

        if (totalDebit != totalCredit)
            throw new InvalidOperationException("Journal entry debit and credit totals must balance.");

        if (totalDebit == 0)
            throw new InvalidOperationException("A journal entry must have a non-zero amount.");
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