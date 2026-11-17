using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

/// <summary>
/// Persistence contract for the Operations module (ribbon completion pass):
/// bank reconciliation marks, POS cash closings, outbox viewer, audit viewers,
/// fiscal roll-over, invoice compression, document repost and support tickets.
/// Implemented provider-neutrally (SQLite + PostgreSQL) in Infrastructure.
/// </summary>
public interface IOperationsRepository
{
    // ------------------------------------------------------------ Bank rec
    /// <summary>Bank GL accounts: banks.gl_account_code when the banks table
    /// exists (migration 031), otherwise accounts flagged allow_reconcile,
    /// otherwise postable asset accounts from chart_of_accounts.</summary>
    Task<IReadOnlyList<BankAccountOption>> GetBankAccountsAsync(string companyId, CancellationToken cancellationToken = default);

    /// <summary>Posted journal lines for one account code, with cleared state.</summary>
    Task<IReadOnlyList<BankJournalLineRow>> GetBankJournalLinesAsync(string companyId, string accountCode, CancellationToken cancellationToken = default);

    /// <summary>Upserts marks in ONE transaction.</summary>
    Task SaveBankMarksAsync(string companyId, IReadOnlyList<BankReconciliationMark> marks, string userId, CancellationToken cancellationToken = default);

    /// <summary>Deletes the marks for the given line keys in ONE transaction.</summary>
    Task UnmarkBankLinesAsync(string companyId, IReadOnlyList<string> lineKeys, string userId, CancellationToken cancellationToken = default);

    // ------------------------------------------------------------ POS closing
    /// <summary>Sales-document totals used as the expected cash for a date:
    /// non-deleted SAL documents of that business date (POS sales are SAL).</summary>
    Task<PosSalesSummary> GetPosSalesSummaryAsync(string companyId, DateOnly businessDate, CancellationToken cancellationToken = default);

    Task SavePosClosingAsync(string companyId, PosCashClosing closing, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PosCashClosing>> GetPosClosingsAsync(string companyId, CancellationToken cancellationToken = default);

    // ------------------------------------------------------------ Outbox
    Task<IReadOnlyList<OutboundMessageRow>> GetOutboundMessagesAsync(string companyId, CancellationToken cancellationToken = default);

    /// <summary>Resets one FAILED outbound message to QUEUED for a real retry.</summary>
    Task RetryOutboundMessageAsync(long messageId, CancellationToken cancellationToken = default);

    // ------------------------------------------------------------ Audit viewers
    Task<IReadOnlyList<SecurityAuditRow>> GetSecurityAuditAsync(string companyId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JournalAuditRow>> GetJournalAuditAsync(string companyId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default);

    // ------------------------------------------------------------ Maintenance
    /// <summary>SQLite only: VACUUM + ANALYZE. Throws NotSupportedException on PostgreSQL.</summary>
    Task VacuumAnalyzeAsync(CancellationToken cancellationToken = default);

    /// <summary>Provider integrity summary (SQLite PRAGMA integrity_check, or PostgreSQL version + table count).</summary>
    Task<string> GetIntegritySummaryAsync(CancellationToken cancellationToken = default);

    // ------------------------------------------------------------ Fiscal roll-over
    Task<IReadOnlyList<FiscalYearRow>> GetFiscalYearsAsync(string companyId, CancellationToken cancellationToken = default);
    Task CreateNextFiscalYearAsync(string companyId, string userId, CancellationToken cancellationToken = default);
    Task CloseFiscalYearAsync(string companyId, long fiscalYearId, string userId, CancellationToken cancellationToken = default);

    // ------------------------------------------------------------ Compression
    Task<CompressCounts> GetCompressCountsAsync(string companyId, CancellationToken cancellationToken = default);

    /// <summary>Hard-deletes soft-deleted DRAFT documents + their lines in ONE
    /// transaction and writes a security_audit_log record in the same
    /// transaction. Posted documents are never touched. Returns rows deleted.</summary>
    Task<int> CompressDeletedDraftsAsync(string companyId, string userId, CancellationToken cancellationToken = default);

    // ------------------------------------------------------------ Repost
    /// <summary>Stored vs line-recomputed totals for one document.
    /// table is "sales" or "purchase". Returns null when not found.</summary>
    Task<DocumentTotalsRow?> GetDocumentTotalsAsync(string companyId, string table, string docNo, CancellationToken cancellationToken = default);

    /// <summary>Writes the recomputed totals transactionally + audit record.</summary>
    Task ApplyRepostAsync(string companyId, string table, DocumentTotalsRow totals, string userId, CancellationToken cancellationToken = default);

    /// <summary>Stored vs recomputed totals for one manufacturing order
    /// (migration 034): computed total = SUM(mfg_order_lines.amount) + overhead.
    /// Returns null when the order (or the mfg schema) does not exist.</summary>
    Task<DocumentTotalsRow?> GetMfgOrderTotalsAsync(string companyId, string docNo, CancellationToken cancellationToken = default);

    /// <summary>Writes the recomputed mfg total_cost + unit_cost transactionally + audit record.</summary>
    Task ApplyMfgRepostAsync(string companyId, DocumentTotalsRow totals, string userId, CancellationToken cancellationToken = default);

    // ------------------------------------------------------------ Support tickets
    /// <summary>Creates a ticket, assigning the next per-company ticket_no inside the transaction.</summary>
    Task<SupportTicket> CreateTicketAsync(string companyId, SupportTicket ticket, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SupportTicket>> GetTicketsAsync(string companyId, CancellationToken cancellationToken = default);
}
