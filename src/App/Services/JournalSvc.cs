using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

public sealed record JournalLineRequest(Guid AccountId, decimal Debit, decimal Credit, string? Note = null, string? CostCenter = null);

public sealed class JournalService
{
    private readonly IJournalRepository _repository;

    public JournalService(IJournalRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public Task<JournalEntrySnapshot?> FindByDocumentNumberAsync(
        Guid companyId,
        string documentNumber,
        CancellationToken cancellationToken = default)
        => FindByDocumentNumberAsync(companyId, documentNumber, null, cancellationToken);

    public Task<JournalEntrySnapshot?> FindByDocumentNumberAsync(
        Guid companyId,
        string documentNumber,
        DateOnly? entryDate,
        CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        if (string.IsNullOrWhiteSpace(documentNumber)) throw new ArgumentException("Document number is required.", nameof(documentNumber));
        return _repository.FindByDocumentNumberAsync(companyId, documentNumber.Trim(), entryDate, cancellationToken);
    }

    public Task<string?> FindAdjacentDocumentNumberAsync(
        Guid companyId,
        string currentDocumentNumber,
        string seriesPrefix,
        int offset,
        CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        if (string.IsNullOrWhiteSpace(seriesPrefix)) throw new ArgumentException("Series prefix is required.", nameof(seriesPrefix));
        return _repository.FindAdjacentDocumentNumberAsync(companyId, currentDocumentNumber, seriesPrefix, offset, cancellationToken);
    }

    public Task<IReadOnlyList<CashMovementRow>> ListSourceVouchersForDateAsync(
        Guid companyId,
        DateOnly entryDate,
        IReadOnlyList<string> seriesPrefixes,
        CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        return _repository.ListSourceVouchersForDateAsync(companyId, entryDate, seriesPrefixes, cancellationToken);
    }

    public async Task<Guid> CreateDraftAsync(
        Guid companyId,
        DateOnly entryDate,
        string description,
        string userId,
        IEnumerable<JournalLineRequest> lines,
        CancellationToken cancellationToken = default,
        string? referenceNumber = null,
        string currencyCode = "SAR",
        decimal exchangeRate = 1m,
        string? documentNumber = null,
        VoucherPartyLinkRequest? partyLink = null)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var entry = new JournalEntry(entryDate, description, referenceNumber, currencyCode, exchangeRate, documentNumber);
        foreach (var line in lines)
            entry.AddLine(line.AccountId, line.Debit, line.Credit, line.Note, line.CostCenter);

        entry.EnsureBalanced();
        await _repository.SaveDraftAsync(companyId, entry, userId, cancellationToken, partyLink).ConfigureAwait(false);
        return entry.Id;
    }

    public async Task UpdateDraftAsync(
        Guid companyId,
        Guid journalEntryId,
        DateOnly entryDate,
        string description,
        string userId,
        IEnumerable<JournalLineRequest> lines,
        CancellationToken cancellationToken = default,
        string? referenceNumber = null,
        string currencyCode = "SAR",
        decimal exchangeRate = 1m)
    {
        if (journalEntryId == Guid.Empty) throw new ArgumentException("Journal entry is required.", nameof(journalEntryId));
        ArgumentNullException.ThrowIfNull(lines);

        var entry = new JournalEntry(entryDate, description, referenceNumber, currencyCode, exchangeRate);
        foreach (var line in lines)
            entry.AddLine(line.AccountId, line.Debit, line.Credit, line.Note, line.CostCenter);

        entry.EnsureBalanced();
        await _repository.UpdateDraftAsync(companyId, journalEntryId, entry, userId, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<BudgetWarning>> PostAsync(
        Guid journalEntryId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (journalEntryId == Guid.Empty) throw new ArgumentException("Journal entry is required.", nameof(journalEntryId));
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));
        return _repository.PostAsync(journalEntryId, userId, cancellationToken);
    }

    public Task<Guid> ReverseAsync(
        Guid journalEntryId,
        string userId,
        string? details = null,
        CancellationToken cancellationToken = default)
    {
        if (journalEntryId == Guid.Empty) throw new ArgumentException("Journal entry is required.", nameof(journalEntryId));
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));
        return _repository.ReverseAsync(journalEntryId, userId, details, cancellationToken);
    }
}
