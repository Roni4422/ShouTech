using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

public interface IJournalRepository
{
    Task<JournalEntrySnapshot?> FindByDocumentNumberAsync(Guid companyId, string documentNumber, CancellationToken cancellationToken = default);
    Task SaveDraftAsync(Guid companyId, JournalEntry entry, string userId, CancellationToken cancellationToken = default);
    Task UpdateDraftAsync(Guid companyId, Guid journalEntryId, JournalEntry entry, string userId, CancellationToken cancellationToken = default);
    /// <summary>Posts a draft entry. Returns the budget warnings the
    /// posting produced in Warn mode (Phase B 055, BudgetGuard) —
    /// empty when no budget warned; a Stop-mode budget refuses the
    /// posting with a cataloged AppError (ACC-5018/5019) instead.</summary>
    Task<IReadOnlyList<BudgetWarning>> PostAsync(Guid journalEntryId, string userId, CancellationToken cancellationToken = default);
    Task<Guid> ReverseAsync(Guid journalEntryId, string userId, string? details = null, CancellationToken cancellationToken = default);

    /// <summary>Posts a source document to the GL in its own transaction
    /// through the Step-3 GlPosting engine (source link, migration 060;
    /// dual base amounts, migration 062). Implemented by
    /// JournalRepository; cheque postings (Step 8) go through here so
    /// every cheque event is its own journal entry.</summary>
    Task<GlPostingResult> PostDocumentAsync(GlDocumentRequest request, string userId, CancellationToken cancellationToken = default);
}
