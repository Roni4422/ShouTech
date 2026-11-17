using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

public interface IJournalRepository
{
    Task<JournalEntrySnapshot?> FindByDocumentNumberAsync(Guid companyId, string documentNumber, CancellationToken cancellationToken = default);
    Task SaveDraftAsync(Guid companyId, JournalEntry entry, string userId, CancellationToken cancellationToken = default);
    Task UpdateDraftAsync(Guid companyId, Guid journalEntryId, JournalEntry entry, string userId, CancellationToken cancellationToken = default);
    Task PostAsync(Guid journalEntryId, string userId, CancellationToken cancellationToken = default);
    Task<Guid> ReverseAsync(Guid journalEntryId, string userId, string? details = null, CancellationToken cancellationToken = default);

    /// <summary>Posts a source document to the GL in its own transaction
    /// through the Step-3 GlPosting engine (source link, migration 060;
    /// dual base amounts, migration 062). Implemented by
    /// JournalRepository; cheque postings (Step 8) go through here so
    /// every cheque event is its own journal entry.</summary>
    Task<GlPostingResult> PostDocumentAsync(GlDocumentRequest request, string userId, CancellationToken cancellationToken = default);
}
