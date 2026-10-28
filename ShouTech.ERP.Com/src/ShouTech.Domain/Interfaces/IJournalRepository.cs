using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

public interface IJournalRepository
{
    Task<JournalEntrySnapshot?> FindByDocumentNumberAsync(Guid companyId, string documentNumber, CancellationToken cancellationToken = default);
    Task SaveDraftAsync(Guid companyId, JournalEntry entry, string userId, CancellationToken cancellationToken = default);
    Task PostAsync(Guid journalEntryId, string userId, CancellationToken cancellationToken = default);
    Task<Guid> ReverseAsync(Guid journalEntryId, string userId, string? details = null, CancellationToken cancellationToken = default);
}
