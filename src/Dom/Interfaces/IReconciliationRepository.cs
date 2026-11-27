using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

public interface IReconciliationRepository
{
    /// <summary>partyKind: "C" = customers (sales documents), "S" = suppliers (purchase documents).</summary>
    Task<IReadOnlyList<OpenDocumentRow>> GetOpenDocumentsAsync(string companyId, string partyKind, CancellationToken cancellationToken = default);

    /// <summary>partyKind: "C" = customers (credit-side ledger entries), "S" = suppliers (debit-side entries).
    /// For customers the list also carries posted credit notes (SRT, G17) at their net credit
    /// (gross credit minus refunds already paid out on the same return document).</summary>
    Task<IReadOnlyList<UnallocatedPaymentRow>> GetPaymentsAsync(string companyId, string partyKind, CancellationToken cancellationToken = default);

    /// <summary>
    /// Allocates part of a payment to an open document in ONE transaction:
    /// the payment, its prior allocations, and the document's open amount are
    /// re-read and re-validated inside the transaction before inserting the
    /// allocation and increasing the document's paid_amount.
    /// Returns the new allocation ID.
    /// W8 (migration 103): <paramref name="discountAmount"/> is a settlement
    /// discount in DOCUMENT currency (settles the document alongside the
    /// payment; posts Dr settlement discount / Cr party control in the
    /// allocation journal). <paramref name="docAmount"/> is the amount
    /// settled on the document in DOCUMENT currency for cross-currency
    /// allocations (<paramref name="amount"/> always stays the
    /// payment-currency amount consumed from the payment); it defaults to
    /// <paramref name="amount"/> and must equal it when the currencies
    /// match. The realized FX difference posts in the same journal.
    /// </summary>
    Task<Guid> AllocateAsync(string companyId, string partyKind, long paymentLedgerId, string docTable, long docId, decimal amount, string userId, string? note = null, decimal discountAmount = 0m, decimal? docAmount = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverses ONE advance allocation (migration 110) in one transaction:
    /// mirrors its clearing journal, releases the document's paid_amount,
    /// writes a migration-079 audit row, and deletes the allocation —
    /// restoring the advance's available balance. Advance allocations
    /// only; returns the clearing entry's reversal journal id.
    /// </summary>
    Task<Guid> UnallocateAsync(string companyId, Guid allocationId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Reverses the most recent advance allocation between one
    /// payment/advance entry and one document (the PayRecWin action).</summary>
    Task<Guid> UnallocateLatestAdvanceAsync(string companyId, long paymentLedgerId, string docTable, long docId, string userId, CancellationToken cancellationToken = default);
}

// ---- merged from src/Domain/Interfaces/IJournalRepository.cs ----
public interface IJournalRepository
{
    Task<JournalEntrySnapshot?> FindByDocumentNumberAsync(Guid companyId, string documentNumber, CancellationToken cancellationToken = default);
    Task<JournalEntrySnapshot?> FindByDocumentNumberAsync(Guid companyId, string documentNumber, DateOnly? entryDate, CancellationToken cancellationToken = default);
    /// <summary>Finds the first/last or adjacent existing document number in one
    /// source series. The source voucher number is used even after its GL
    /// document_number is replaced by the central JRN number.</summary>
    Task<string?> FindAdjacentDocumentNumberAsync(Guid companyId, string currentDocumentNumber, string seriesPrefix, int offset, CancellationToken cancellationToken = default);
    /// <summary>Saves a draft journal entry in ONE transaction. When
    /// <paramref name="partyLink"/> is supplied (cash vouchers, G23) the
    /// voucher_party_links row is written inside that same transaction,
    /// so a draft can never exist without its intended party link.</summary>
    Task SaveDraftAsync(Guid companyId, JournalEntry entry, string userId, CancellationToken cancellationToken = default, VoucherPartyLinkRequest? partyLink = null);
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

// ---- merged from src/Domain/Interfaces/IChequeRepository.cs ----
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

// ---- merged from src/Domain/Interfaces/IBudgetRepository.cs ----
/// <summary>Budget persistence (migration 055). Budgets live in the
/// journal world, so every method keys the company by its Guid —
/// unlike the master-card repositories, which key by company code.
/// The code-keyed lookups (cost centers, projects) resolve the code
/// from the companies registry inside the repository.</summary>
public interface IBudgetRepository
{
    /// <summary>All live budgets of the company with their lines and
    /// monthly distributions loaded.</summary>
    Task<IReadOnlyList<Budget>> GetAllAsync(Guid companyId, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces a budget (header + lines +
    /// distributions) in one transaction, and sets the chart budget
    /// opt-in flag on every account that gains a line.</summary>
    Task SaveAsync(Guid companyId, Budget budget, string userId, CancellationToken cancellationToken = default);

    Task SoftDeleteAsync(Guid companyId, Guid budgetId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Moves a budget between Draft / Active / Closed.</summary>
    Task SetStatusAsync(Guid companyId, Guid budgetId, string status, string userId, CancellationToken cancellationToken = default);

    /// <summary>Budget vs actual for every live budget line, computed
    /// by the same actuals implementation the posting guard enforces
    /// (BudgetGuard.GetLineActuals).</summary>
    Task<IReadOnlyList<BudgetVarianceRow>> GetVarianceAsync(Guid companyId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BudgetFiscalYear>> GetFiscalYearsAsync(Guid companyId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BudgetAccountOption>> GetAccountsAsync(Guid companyId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BudgetDimensionOption>> GetCostCentersAsync(Guid companyId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BudgetDimensionOption>> GetProjectsAsync(Guid companyId, CancellationToken cancellationToken = default);
}
