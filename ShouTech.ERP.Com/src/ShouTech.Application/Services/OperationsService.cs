using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>
/// Operations service: bank reconciliation marks, POS cash closings, outbox
/// retry, audit viewers, fiscal roll-over, invoice compression, document
/// repost and support tickets. All invariants are validated here before the
/// repository is touched; money math (closing difference, bank totals) is
/// computed here so it is unit-testable without a database.
/// </summary>
public sealed class OperationsService
{
    private readonly IOperationsRepository _repository;
    public OperationsService(IOperationsRepository repository)
        => _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    internal IOperationsRepository Repository => _repository;

    // ------------------------------------------------------------ Bank rec

    public Task<IReadOnlyList<BankAccountOption>> GetBankAccountsAsync(string companyId, CancellationToken ct = default)
        => _repository.GetBankAccountsAsync(RequireCompany(companyId), ct);

    public Task<IReadOnlyList<BankJournalLineRow>> GetBankJournalLinesAsync(string companyId, string accountCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accountCode))
            throw new InvalidOperationException("A bank account code is required.");
        return _repository.GetBankJournalLinesAsync(RequireCompany(companyId), accountCode.Trim(), ct);
    }

    /// <summary>Totals over a loaded line set: book = all lines, cleared =
    /// marked lines, uncleared = the rest. Pure function of the rows.</summary>
    public static BankReconciliationTotals ComputeTotals(IEnumerable<BankJournalLineRow> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        decimal book = 0m, cleared = 0m;
        foreach (var line in lines)
        {
            book += line.Amount;
            if (line.IsCleared)
                cleared += line.Amount;
        }
        return new BankReconciliationTotals(book, cleared, book - cleared);
    }

    public async Task SaveBankMarksAsync(string companyId, IReadOnlyList<BankReconciliationMark> marks, string userId, CancellationToken ct = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(marks);
        foreach (var mark in marks)
        {
            if (string.IsNullOrWhiteSpace(mark.JournalLineKey))
                throw new InvalidOperationException("Every reconciliation mark needs a journal line.");
            if (string.IsNullOrWhiteSpace(mark.AccountCode))
                throw new InvalidOperationException("Every reconciliation mark needs an account code.");
        }
        await _repository.SaveBankMarksAsync(companyId, marks, RequireUser(userId), ct).ConfigureAwait(false);
    }

    public Task UnmarkBankLinesAsync(string companyId, IReadOnlyList<string> lineKeys, string userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lineKeys);
        if (lineKeys.Count == 0)
            throw new InvalidOperationException("Select at least one cleared line to unmark.");
        return _repository.UnmarkBankLinesAsync(RequireCompany(companyId), lineKeys, RequireUser(userId), ct);
    }

    // ------------------------------------------------------------ POS closing

    public Task<PosSalesSummary> GetPosSalesSummaryAsync(string companyId, DateOnly businessDate, CancellationToken ct = default)
        => _repository.GetPosSalesSummaryAsync(RequireCompany(companyId), businessDate, ct);

    /// <summary>Expected cash = opening float + POS sales for the date.</summary>
    public static decimal ComputeExpected(decimal openingAmount, PosSalesSummary sales) => openingAmount + sales.Total;

    /// <summary>Closing difference = counted - expected (negative = short).</summary>
    public static decimal ComputeDifference(decimal countedAmount, decimal expectedAmount) => countedAmount - expectedAmount;

    public async Task<Guid> SavePosClosingAsync(string companyId, PosCashClosing closing, string userId, CancellationToken ct = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(closing);
        if (string.IsNullOrWhiteSpace(closing.TerminalCode))
            throw new InvalidOperationException("Terminal code is required.");
        if (closing.OpeningAmount < 0 || closing.ExpectedAmount < 0 || closing.CountedAmount < 0)
            throw new InvalidOperationException("Closing amounts cannot be negative.");
        closing.TerminalCode = closing.TerminalCode.Trim();
        closing.Difference = ComputeDifference(closing.CountedAmount, closing.ExpectedAmount);
        await _repository.SavePosClosingAsync(companyId, closing, RequireUser(userId), ct).ConfigureAwait(false);
        return closing.Id;
    }

    public Task<IReadOnlyList<PosCashClosing>> GetPosClosingsAsync(string companyId, CancellationToken ct = default)
        => _repository.GetPosClosingsAsync(RequireCompany(companyId), ct);

    // ------------------------------------------------------------ Outbox / audit

    public Task<IReadOnlyList<OutboundMessageRow>> GetOutboundMessagesAsync(string companyId, CancellationToken ct = default)
        => _repository.GetOutboundMessagesAsync(RequireCompany(companyId), ct);

    public Task RetryOutboundMessageAsync(long messageId, CancellationToken ct = default)
    {
        if (messageId <= 0)
            throw new ArgumentException("Message is required.", nameof(messageId));
        return _repository.RetryOutboundMessageAsync(messageId, ct);
    }

    public Task<IReadOnlyList<SecurityAuditRow>> GetSecurityAuditAsync(string companyId, DateOnly? from, DateOnly? to, CancellationToken ct = default)
        => _repository.GetSecurityAuditAsync(RequireCompany(companyId), from, to, ct);

    public Task<IReadOnlyList<JournalAuditRow>> GetJournalAuditAsync(string companyId, DateOnly? from, DateOnly? to, CancellationToken ct = default)
        => _repository.GetJournalAuditAsync(RequireCompany(companyId), from, to, ct);

    // ------------------------------------------------------------ Maintenance / fiscal

    public Task VacuumAnalyzeAsync(CancellationToken ct = default) => _repository.VacuumAnalyzeAsync(ct);
    public Task<string> GetIntegritySummaryAsync(CancellationToken ct = default) => _repository.GetIntegritySummaryAsync(ct);

    public Task<IReadOnlyList<FiscalYearRow>> GetFiscalYearsAsync(string companyId, CancellationToken ct = default)
        => _repository.GetFiscalYearsAsync(RequireCompany(companyId), ct);

    public Task CreateNextFiscalYearAsync(string companyId, string userId, CancellationToken ct = default)
        => _repository.CreateNextFiscalYearAsync(RequireCompany(companyId), RequireUser(userId), ct);

    public Task CloseFiscalYearAsync(string companyId, long fiscalYearId, string userId, CancellationToken ct = default)
    {
        if (fiscalYearId <= 0)
            throw new ArgumentException("Fiscal year is required.", nameof(fiscalYearId));
        return _repository.CloseFiscalYearAsync(RequireCompany(companyId), fiscalYearId, RequireUser(userId), ct);
    }

    // ------------------------------------------------------------ Compression / repost

    public Task<CompressCounts> GetCompressCountsAsync(string companyId, CancellationToken ct = default)
        => _repository.GetCompressCountsAsync(RequireCompany(companyId), ct);

    public Task<int> CompressDeletedDraftsAsync(string companyId, string userId, CancellationToken ct = default)
        => _repository.CompressDeletedDraftsAsync(RequireCompany(companyId), RequireUser(userId), ct);

    public Task<DocumentTotalsRow?> GetDocumentTotalsAsync(string companyId, string table, string docNo, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(docNo))
            throw new InvalidOperationException("Document number is required.");
        return _repository.GetDocumentTotalsAsync(RequireCompany(companyId), table, docNo.Trim(), ct);
    }

    public async Task ApplyRepostAsync(string companyId, string table, DocumentTotalsRow totals, string userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(totals);
        if (!totals.Differs)
            throw new InvalidOperationException("Stored totals already match the lines; nothing to repost.");
        await _repository.ApplyRepostAsync(RequireCompany(companyId), table, totals, RequireUser(userId), ct).ConfigureAwait(false);
    }

    public Task<DocumentTotalsRow?> GetMfgOrderTotalsAsync(string companyId, string docNo, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(docNo))
            throw new InvalidOperationException("Manufacturing order number is required.");
        return _repository.GetMfgOrderTotalsAsync(RequireCompany(companyId), docNo.Trim(), ct);
    }

    public async Task ApplyMfgRepostAsync(string companyId, DocumentTotalsRow totals, string userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(totals);
        if (!totals.Differs)
            throw new InvalidOperationException("Stored totals already match the lines; nothing to repost.");
        await _repository.ApplyMfgRepostAsync(RequireCompany(companyId), totals, RequireUser(userId), ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ Support tickets

    public async Task<SupportTicket> CreateTicketAsync(string companyId, SupportTicket ticket, string userId, CancellationToken ct = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(ticket);
        if (!SupportTicketKinds.All.Contains(ticket.Kind))
            throw new InvalidOperationException($"Unknown ticket kind '{ticket.Kind}'.");
        if (string.IsNullOrWhiteSpace(ticket.Subject))
            throw new InvalidOperationException("Ticket subject is required.");
        if (string.IsNullOrWhiteSpace(ticket.Body))
            throw new InvalidOperationException("Ticket details are required.");
        if (!string.IsNullOrWhiteSpace(ticket.ContactEmail) && !ticket.ContactEmail.Contains('@'))
            throw new InvalidOperationException("Contact e-mail does not look valid.");
        ticket.Subject = ticket.Subject.Trim();
        ticket.Body = ticket.Body.Trim();
        return await _repository.CreateTicketAsync(companyId, ticket, RequireUser(userId), ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<SupportTicket>> GetTicketsAsync(string companyId, CancellationToken ct = default)
        => _repository.GetTicketsAsync(RequireCompany(companyId), ct);

    private static string RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
        return companyId;
    }

    private static string RequireUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException("A current user is required for audited writes.");
        return userId;
    }
}
