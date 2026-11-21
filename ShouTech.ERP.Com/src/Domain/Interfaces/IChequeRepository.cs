using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

/// <summary>
/// Persistence contract for the Cheque module (ribbon completion pass).
/// Implemented provider-neutrally (SQLite + PostgreSQL) in Infrastructure.
/// Company id here is the company code stored in cheques.company_id (the same
/// convention as projects / material stock); journal company/account
/// resolution to GUIDs is exposed separately for the journal engine.
/// </summary>
public interface IChequeRepository
{
    Task<IReadOnlyList<Cheque>> GetAllAsync(string companyId, string? status = null, string? direction = null, CancellationToken cancellationToken = default);

    Task<Cheque?> GetByIdAsync(string companyId, Guid chequeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a batch in one transaction. The duplicate guard
    /// (company + bank + drawer + cheque no among live rows) is re-checked
    /// inside the transaction in addition to the partial unique index.
    /// </summary>
    Task InsertBatchAsync(string companyId, IReadOnlyList<Cheque> cheques, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compare-and-swap lifecycle update: succeeds only when the stored status
    /// still equals <paramref name="expectedStatus"/> inside the transaction,
    /// so a stale window can never move a cheque twice.
    /// </summary>
    Task UpdateTransitionAsync(string companyId, Cheque cheque, string expectedStatus, string userId, CancellationToken cancellationToken = default);

    /// <summary>Resolves a company code to the GUID used by journal_entries.</summary>
    Task<Guid> ResolveCompanyGuidAsync(string companyCode, CancellationToken cancellationToken = default);

    /// <summary>Resolves an active, postable GL account code to its GUID.</summary>
    Task<Guid> ResolveAccountIdAsync(Guid companyGuid, string accountCode, CancellationToken cancellationToken = default);

    /// <summary>How many live cheques reference the given journal entry.
    /// Step 8 posts one entry per cheque, so a count above 1 identifies a
    /// historical (pre-Step-8) batch entry shared by siblings — such an
    /// entry must never be reversed for a single cheque.</summary>
    Task<int> CountByJournalEntryAsync(string companyId, Guid journalEntryId, CancellationToken cancellationToken = default);
}
