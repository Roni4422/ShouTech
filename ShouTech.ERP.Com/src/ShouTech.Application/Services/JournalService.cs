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
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        if (string.IsNullOrWhiteSpace(documentNumber)) throw new ArgumentException("Document number is required.", nameof(documentNumber));
        return _repository.FindByDocumentNumberAsync(companyId, documentNumber.Trim(), cancellationToken);
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
        string? documentNumber = null)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var entry = new JournalEntry(entryDate, description, referenceNumber, currencyCode, exchangeRate, documentNumber);
        foreach (var line in lines)
            entry.AddLine(line.AccountId, line.Debit, line.Credit, line.Note, line.CostCenter);

        entry.EnsureBalanced();
        await _repository.SaveDraftAsync(companyId, entry, userId, cancellationToken).ConfigureAwait(false);
        return entry.Id;
    }

    public Task PostAsync(Guid journalEntryId, string userId, CancellationToken cancellationToken = default) =>
        _repository.PostAsync(journalEntryId, userId, cancellationToken);

    public Task<Guid> ReverseAsync(Guid journalEntryId, string userId, string? details = null, CancellationToken cancellationToken = default) =>
        _repository.ReverseAsync(journalEntryId, userId, details, cancellationToken);
}
