using ShouTech.Domain;
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

    /// <summary>
    /// The Recalculate Costs engine lives in the Infrastructure layer,
    /// which this App-layer service must not reference; the host
    /// (AppHost) injects it as a delegate bound to the same
    /// provider/connection as the repository. Null in unit tests that
    /// do not exercise recalculation.
    /// </summary>
    private readonly Func<string, DateOnly, string?, string, CancellationToken, Task<RecalculationOutcome>>? _recalculateCosts;

    public OperationsService(
        IOperationsRepository repository,
        Func<string, DateOnly, string?, string, CancellationToken, Task<RecalculationOutcome>>? recalculateCosts = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _recalculateCosts = recalculateCosts;
    }

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

    /// <summary>
    /// Reconciliation totals against ONE bank statement (Step 8, BNK-01).
    /// Book side: book = all loaded book lines, cleared = lines matched
    /// to a statement line of THIS statement, uncleared = the rest.
    /// Statement side: the statement's own opening/closing balances and
    /// the sums of its matched/unmatched lines. Difference (on the
    /// record) = closing − opening − cleared: the statement movement
    /// not yet matched in the books — a real quantity that can be
    /// non-zero, moves by each matched amount, and is legitimately
    /// zero only at a full match. Pure function of the rows.
    /// </summary>
    public static BankReconciliationTotals ComputeTotals(
        IEnumerable<BankJournalLineRow> lines,
        BankStatement? statement,
        IEnumerable<BankStatementLine>? statementLines = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var matchedKeys = new HashSet<string>(StringComparer.Ordinal);
        decimal matchedStatement = 0m, unmatchedStatement = 0m;
        if (statementLines is not null)
        {
            foreach (var sl in statementLines)
            {
                if (sl.IsMatched)
                {
                    matchedStatement += sl.Amount;
                    matchedKeys.Add(sl.MatchedJournalLineKey!);
                }
                else
                {
                    unmatchedStatement += sl.Amount;
                }
            }
        }
        decimal book = 0m, cleared = 0m;
        foreach (var line in lines)
        {
            book += line.Amount;
            // With a statement in scope, "cleared" means matched to THIS
            // statement; without one, fall back to any persisted mark.
            var isCleared = statement is null ? line.IsCleared : matchedKeys.Contains(line.LineKey);
            if (isCleared)
                cleared += line.Amount;
        }
        return new BankReconciliationTotals(
            book, cleared, book - cleared,
            statement?.ClosingBalance ?? 0m, statement?.OpeningBalance ?? 0m,
            matchedStatement, unmatchedStatement);
    }

    // ------------------------------------------------------------ Bank statements (063)

    public async Task<Guid> SaveBankStatementAsync(string companyId, BankStatement statement, string userId, CancellationToken ct = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(statement);
        if (string.IsNullOrWhiteSpace(statement.AccountCode))
            throw new InvalidOperationException("A bank account code is required for a statement.");
        if (statement.PeriodFrom is { } from && statement.PeriodTo is { } to && to < from)
            throw new InvalidOperationException("The statement period end cannot precede its start.");
        statement.AccountCode = statement.AccountCode.Trim();
        await _repository.SaveBankStatementAsync(companyId, statement, RequireUser(userId), ct).ConfigureAwait(false);
        return statement.Id;
    }

    public Task<IReadOnlyList<BankStatement>> GetBankStatementsAsync(string companyId, string accountCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accountCode))
            throw new InvalidOperationException("A bank account code is required.");
        return _repository.GetBankStatementsAsync(RequireCompany(companyId), accountCode.Trim(), ct);
    }

    public Task<BankStatement?> GetBankStatementAsync(string companyId, Guid statementId, CancellationToken ct = default)
    {
        if (statementId == Guid.Empty) throw new ArgumentException("Statement is required.", nameof(statementId));
        return _repository.GetBankStatementAsync(RequireCompany(companyId), statementId, ct);
    }

    public async Task<Guid> AddBankStatementLineAsync(string companyId, BankStatementLine line, string userId, CancellationToken ct = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(line);
        if (line.StatementId == Guid.Empty)
            throw new InvalidOperationException("A statement line needs its statement.");
        if (string.IsNullOrWhiteSpace(line.AccountCode))
            throw new InvalidOperationException("A statement line needs an account code.");
        if (line.Amount == 0m)
            throw new InvalidOperationException("A statement line amount cannot be zero.");
        line.AccountCode = line.AccountCode.Trim();
        return await _repository.AddBankStatementLineAsync(companyId, line, RequireUser(userId), ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<BankStatementLine>> GetBankStatementLinesAsync(string companyId, Guid statementId, CancellationToken ct = default)
    {
        if (statementId == Guid.Empty) throw new ArgumentException("Statement is required.", nameof(statementId));
        return _repository.GetBankStatementLinesAsync(RequireCompany(companyId), statementId, ct);
    }

    public Task RemoveBankStatementLineAsync(string companyId, Guid lineId, string userId, CancellationToken ct = default)
    {
        if (lineId == Guid.Empty) throw new ArgumentException("Statement line is required.", nameof(lineId));
        return _repository.RemoveBankStatementLineAsync(RequireCompany(companyId), lineId, RequireUser(userId), ct);
    }

    /// <summary>Matches one statement line to one book line. The two
    /// amounts must be EQUAL — a partial or mismatched match would hide
    /// exactly the discrepancy the Difference exists to surface; a
    /// genuinely different bank amount stays unmatched until the book
    /// side is corrected or an adjustment entry is posted.</summary>
    public async Task MatchBankLineAsync(string companyId, BankStatementLine statementLine, BankJournalLineRow bookLine, Guid statementId, string userId, CancellationToken ct = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(statementLine);
        ArgumentNullException.ThrowIfNull(bookLine);
        if (statementLine.IsMatched)
            throw new AppError(ErrorCodes.StatementLineAlreadyMatched,
                "The statement line is already matched to a book line; unmatch it first.", statementLine.Id.ToString("D"));
        if (bookLine.IsCleared)
            throw new InvalidOperationException("The book line is already cleared by another statement line or mark.");
        if (statementLine.Amount != bookLine.Amount)
            throw new InvalidOperationException(
                $"Statement amount {statementLine.Amount} does not equal the book line amount {bookLine.Amount}; only equal amounts can be matched.");
        var statement = await _repository.GetBankStatementAsync(companyId, statementId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Bank statement was not found for this company.");
        var mark = new BankReconciliationMark
        {
            JournalEntryId = bookLine.EntryId,
            JournalLineKey = bookLine.LineKey,
            AccountCode = statement.AccountCode,
            StatementDate = statement.StatementDate,
            StatementRef = statement.StatementRef,
            ClearedAmount = bookLine.Amount,
            StatementId = statementId,
            StatementLineId = statementLine.Id
        };
        await _repository.MatchBankLineAsync(companyId, statementLine.Id, mark, RequireUser(userId), ct).ConfigureAwait(false);
    }

    public Task UnmatchBankLineAsync(string companyId, Guid statementLineId, string userId, CancellationToken ct = default)
    {
        if (statementLineId == Guid.Empty) throw new ArgumentException("Statement line is required.", nameof(statementLineId));
        return _repository.UnmatchBankLineAsync(RequireCompany(companyId), statementLineId, RequireUser(userId), ct);
    }

    /// <summary>Closes a statement once it fully reconciles: the
    /// difference must be exactly zero (ACC-5016 otherwise) — a
    /// statement that still disagrees with the books stays open.</summary>
    public async Task CloseBankStatementAsync(string companyId, Guid statementId, string userId, CancellationToken ct = default)
    {
        RequireCompany(companyId);
        if (statementId == Guid.Empty) throw new ArgumentException("Statement is required.", nameof(statementId));
        var statement = await _repository.GetBankStatementAsync(companyId, statementId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Bank statement was not found for this company.");
        if (statement.Status == BankStatementStatuses.Closed)
            throw new InvalidOperationException("Bank statement is already closed.");
        var bookLines = await _repository.GetBankJournalLinesAsync(companyId, statement.AccountCode, ct).ConfigureAwait(false);
        var statementLines = await _repository.GetBankStatementLinesAsync(companyId, statementId, ct).ConfigureAwait(false);
        var totals = ComputeTotals(bookLines, statement, statementLines);
        if (totals.Difference != 0m)
            throw new AppError(ErrorCodes.BankStatementNotReconciled,
                $"The statement cannot be closed: its difference is {totals.Difference}, not zero. Match the remaining statement movement first.",
                statementId.ToString("D"));
        await _repository.CloseBankStatementAsync(companyId, statementId, RequireUser(userId), ct).ConfigureAwait(false);
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

    /// <summary>Read-only trial-balance integrity audit (B5).</summary>
    public Task<TrialBalanceIntegrityResult> GetTrialBalanceIntegrityAsync(string companyId, CancellationToken ct = default)
        => _repository.GetTrialBalanceIntegrityAsync(RequireCompany(companyId), ct);

    /// <summary>Applied-migration facts (count + highest id) for the Tools health panel.</summary>
    public Task<SchemaVersionInfo> GetSchemaVersionAsync(CancellationToken ct = default)
        => _repository.GetSchemaVersionAsync(ct);

    /// <summary>
    /// Recalculate Costs (B5): replays stock valuation for the whole
    /// company from <paramref name="fromDate"/> onward through the
    /// injected engine. The engine records the job itself
    /// (stock_revaluation_jobs) and refuses ranges touching a closed
    /// fiscal period (INV-6004).
    /// </summary>
    public async Task<RecalculationOutcome> RecalculateCostsAsync(
        string companyId, DateOnly fromDate, string? reason, string userId, CancellationToken ct = default)
    {
        var company = RequireCompany(companyId);
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("A user is required.", nameof(userId));
        if (_recalculateCosts is null)
            throw new InvalidOperationException("Recalculate Costs is not configured for this host.");
        return await _recalculateCosts(company, fromDate, reason, userId.Trim(), ct).ConfigureAwait(false);
    }

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
