namespace ShouTech.Domain.Entities;

/// <summary>Ticket kinds stored in support_tickets.kind.</summary>
public static class SupportTicketKinds
{
    public const string Support = "SUPPORT";
    public const string Suggestion = "SUGGESTION";
    public static readonly IReadOnlyList<string> All = new[] { Support, Suggestion };
}

/// <summary>A bank account choice for reconciliation (code + display name).</summary>
public sealed class BankAccountOption
{
    public string AccountCode { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>
/// One posted journal line for a bank account, with its reconciliation state.
/// LineKey is CAST(journal_entry_line_id AS TEXT) — the stable surrogate key
/// documented in migration 035.
/// </summary>
public sealed class BankJournalLineRow
{
    public string LineKey { get; set; } = string.Empty;
    public string EntryId { get; set; } = string.Empty;
    public string EntryDate { get; set; } = string.Empty;
    public string DocNo { get; set; } = string.Empty;
    public string Memo { get; set; } = string.Empty;
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public bool IsCleared { get; set; }
    public string? StatementDate { get; set; }
    public string? StatementRef { get; set; }
    /// <summary>UI-only checkbox state (not persisted directly).</summary>
    public bool Selected { get; set; }
    public decimal Amount => Debit - Credit;
}

/// <summary>A persisted cleared mark (migration 035; statement link
/// added by migration 063).</summary>
public sealed class BankReconciliationMark
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string JournalEntryId { get; set; } = string.Empty;
    public string JournalLineKey { get; set; } = string.Empty;
    public string AccountCode { get; set; } = string.Empty;
    public DateOnly StatementDate { get; set; }
    public string? StatementRef { get; set; }
    public decimal ClearedAmount { get; set; }
    /// <summary>The bank statement this mark belongs to (063), when the
    /// clearing came from statement matching rather than a bare mark.</summary>
    public Guid? StatementId { get; set; }
    /// <summary>The statement line that cleared this book line (063).</summary>
    public Guid? StatementLineId { get; set; }
}

/// <summary>Statement statuses (migration 063).</summary>
public static class BankStatementStatuses
{
    public const string Open = "Open";
    public const string Closed = "Closed";
    public static readonly IReadOnlyList<string> All = new[] { Open, Closed };
}

/// <summary>
/// One bank statement (migration 063): the bank's side of a
/// reconciliation, with its own identity, period and balances. This is
/// the object whose absence made the old "Difference" identically zero
/// (BNK-01): there was no statement balance to differ from the books.
/// </summary>
public sealed class BankStatement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string AccountCode { get; set; } = string.Empty;
    public DateOnly StatementDate { get; set; }
    public string? StatementRef { get; set; }
    public DateOnly? PeriodFrom { get; set; }
    public DateOnly? PeriodTo { get; set; }
    /// <summary>Balance at the start of the statement period (the prior
    /// statement's closing balance, or the account's opening figure).</summary>
    public decimal OpeningBalance { get; set; }
    /// <summary>The closing balance AS PRINTED ON THE STATEMENT — bank
    /// data, independent of the books.</summary>
    public decimal ClosingBalance { get; set; }
    public string Status { get; set; } = BankStatementStatuses.Open;
}

/// <summary>
/// One line of a bank statement (migration 063): a movement the bank
/// recorded, entered in BankRecWin (file import is a flagged gap).
/// Amount is signed in bank-account terms: positive = money into the
/// account (a book debit), negative = money out (a book credit), so a
/// statement line matches a book line when their amounts are equal.
/// </summary>
public sealed class BankStatementLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StatementId { get; set; }
    public string AccountCode { get; set; } = string.Empty;
    public DateOnly LineDate { get; set; }
    public string? Reference { get; set; }
    public string? Description { get; set; }
    public decimal Amount { get; set; }
    /// <summary>The book (journal) line key this statement line is
    /// matched to, or null while unmatched.</summary>
    public string? MatchedJournalLineKey { get; set; }
    public bool IsMatched => !string.IsNullOrWhiteSpace(MatchedJournalLineKey);
}

/// <summary>
/// Reconciliation totals for one bank account against ONE statement
/// (Step 8, BNK-01). BookBalance / ClearedTotal / UnclearedTotal are the
/// book side; StatementBalance is the statement's closing balance.
/// Difference = StatementBalance − OpeningBalance − ClearedTotal: the
/// statement movement not yet matched in the books. It is a real
/// computed quantity — non-zero whenever the bank and the books
/// disagree, moving by each matched amount as matching proceeds, and
/// legitimately zero only when the whole statement movement is matched.
/// (The pre-Step-8 formula Book − Cleared − Uncleared was identically
/// zero for every dataset and has been removed.)
/// </summary>
public sealed record BankReconciliationTotals(
    decimal BookBalance, decimal ClearedTotal, decimal UnclearedTotal,
    decimal StatementBalance, decimal OpeningBalance,
    decimal MatchedStatementTotal, decimal UnmatchedStatementTotal)
{
    public decimal Difference => StatementBalance - OpeningBalance - ClearedTotal;
    /// <summary>Statement movement (closing − opening) for comparison
    /// with the sum of the entered statement lines.</summary>
    public decimal StatementMovement => StatementBalance - OpeningBalance;
}

/// <summary>One POS cash closing (migration 037). Difference = Counted - Expected.</summary>
public sealed class PosCashClosing
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TerminalCode { get; set; } = string.Empty;
    public DateOnly BusinessDate { get; set; }
    public string? OpenedBy { get; set; }
    public string? ClosedBy { get; set; }
    public decimal OpeningAmount { get; set; }
    public decimal ExpectedAmount { get; set; }
    public decimal CountedAmount { get; set; }
    public decimal Difference { get; set; }
    public string Status { get; set; } = "Closed";
    public string? Notes { get; set; }
}

/// <summary>Expected POS sales for one business date (count + grand total).</summary>
public sealed record PosSalesSummary(int DocumentCount, decimal Total);

/// <summary>One row of the outbound_messages outbox (migration 006).</summary>
public sealed class OutboundMessageRow
{
    public long Id { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string Recipient { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; }
    public string? LastError { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string? NextAttemptAt { get; set; }
}

/// <summary>One security_audit_log row (migration 029).</summary>
public sealed class SecurityAuditRow
{
    public long Id { get; set; }
    public string OccurredAt { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string? Details { get; set; }
}

/// <summary>One journal_audit_events row (001 core).</summary>
public sealed class JournalAuditRow
{
    public long Id { get; set; }
    public string EntryId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string EventAt { get; set; } = string.Empty;
    public string? Details { get; set; }
}

/// <summary>One fiscal_years row for the roll-over window.</summary>
public sealed class FiscalYearRow
{
    public long Id { get; set; }
    public int FiscalYear { get; set; }
    public string StartDate { get; set; } = string.Empty;
    public string EndDate { get; set; } = string.Empty;
    public bool IsCurrent { get; set; }
    public bool IsClosed { get; set; }
    public string? ClosedBy { get; set; }
    public string? ClosedAt { get; set; }
    /// <summary>Period-end activation control (migration 127, study SS7):
    /// ON by default for every year; deactivating it is the audited
    /// roll-forward into the next financial year.</summary>
    public bool PeriodEndActivation { get; set; } = true;
}

/// <summary>One fiscal_periods row for the period-close window
/// (docs/PERIOD_CLOSE_STUDY.md).</summary>
public sealed class FiscalPeriodRow
{
    public Guid Id { get; set; }
    public long FiscalYearId { get; set; }
    public int PeriodNumber { get; set; }
    public string PeriodName { get; set; } = string.Empty;
    public string StartDate { get; set; } = string.Empty;
    public string EndDate { get; set; } = string.Empty;
    /// <summary>OPEN / CLOSED / LOCKED.</summary>
    public string Status { get; set; } = string.Empty;
    public string? ClosedBy { get; set; }
    public string? ClosedAt { get; set; }
}

/// <summary>One unposted item dated inside a fiscal period — a draft
/// journal assigned to the period, or a draft document dated in its
/// range. Shown by the period-close window before closing and counted
/// by the close engine's ACC-5022 refusal.</summary>
public sealed class PeriodUnpostedItemRow
{
    /// <summary>JOURNAL or DOCUMENT.</summary>
    public string Kind { get; set; } = string.Empty;
    public string DocKind { get; set; } = string.Empty;
    public string DocNo { get; set; } = string.Empty;
    public string DocDate { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

/// <summary>Soft-deleted draft counts per document table (compression preview).</summary>
public sealed record CompressCounts(int SalesDrafts, int PurchaseDrafts)
{
    public int Total => SalesDrafts + PurchaseDrafts;
}

/// <summary>One fiscal period's posted totals in the trial-balance
/// integrity audit (Maintenance, services phase B5).</summary>
public sealed class TrialBalancePeriodRow
{
    /// <summary>Period name, or the no-period bucket label.</summary>
    public string Period { get; set; } = string.Empty;
    /// <summary>Fiscal period status (OPEN / CLOSED / LOCKED), or empty
    /// for the no-period bucket — reports mark closed periods.</summary>
    public string Status { get; set; } = string.Empty;
    public int Entries { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public bool IsBalanced => Math.Abs(Debit - Credit) <= 0.004m;
}

/// <summary>One posted journal entry whose lines do not balance
/// (trial-balance integrity audit detail).</summary>
public sealed class UnbalancedEntryRow
{
    public string EntryId { get; set; } = string.Empty;
    public string EntryDate { get; set; } = string.Empty;
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
}

/// <summary>Outcome of one Recalculate Costs job (the valuation
/// replay engine's result, projected into the App layer).</summary>
public sealed record RecalculationOutcome(string JobId, int MovementsReplayed, decimal ValueDelta);

/// <summary>Read-only trial-balance integrity audit result: per-period
/// posted Dr=Cr verification, orphan journal lines (a line whose entry
/// no longer exists), and posted entries carrying no lines at all.</summary>
public sealed class TrialBalanceIntegrityResult
{
    public IReadOnlyList<TrialBalancePeriodRow> Periods { get; set; } = Array.Empty<TrialBalancePeriodRow>();
    public IReadOnlyList<UnbalancedEntryRow> UnbalancedEntries { get; set; } = Array.Empty<UnbalancedEntryRow>();
    public int OrphanLineCount { get; set; }
    public int PostedEntriesWithoutLines { get; set; }
    public bool IsClean =>
        OrphanLineCount == 0 && PostedEntriesWithoutLines == 0 &&
        UnbalancedEntries.Count == 0 && Periods.All(p => p.IsBalanced);
}

/// <summary>Stored vs recomputed totals of one commercial document (repost).</summary>
public sealed class DocumentTotalsRow
{
    public long DocId { get; set; }
    public string DocKind { get; set; } = string.Empty;
    public string DocNo { get; set; } = string.Empty;
    public string DocDate { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal StoredSubtotal { get; set; }
    public decimal StoredDiscount { get; set; }
    public decimal StoredTax { get; set; }
    public decimal StoredGrand { get; set; }
    public decimal ComputedSubtotal { get; set; }
    public decimal ComputedDiscount { get; set; }
    public decimal ComputedTax { get; set; }
    public decimal ComputedGrand { get; set; }
    public int LineCount { get; set; }
    public bool Differs =>
        Math.Abs(StoredSubtotal - ComputedSubtotal) > 0.005m ||
        Math.Abs(StoredDiscount - ComputedDiscount) > 0.005m ||
        Math.Abs(StoredTax - ComputedTax) > 0.005m ||
        Math.Abs(StoredGrand - ComputedGrand) > 0.005m;
}

/// <summary>One locally persisted support / suggestion ticket (migration 037).</summary>
public sealed class SupportTicket
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int TicketNo { get; set; }
    public string Kind { get; set; } = SupportTicketKinds.Support;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? ContactEmail { get; set; }
    public string Status { get; set; } = "Open";
    public string CreatedAt { get; set; } = string.Empty;
}
