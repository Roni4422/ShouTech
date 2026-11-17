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

/// <summary>A persisted cleared mark (migration 035).</summary>
public sealed class BankReconciliationMark
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string JournalEntryId { get; set; } = string.Empty;
    public string JournalLineKey { get; set; } = string.Empty;
    public string AccountCode { get; set; } = string.Empty;
    public DateOnly StatementDate { get; set; }
    public string? StatementRef { get; set; }
    public decimal ClearedAmount { get; set; }
}

/// <summary>Book / cleared / uncleared totals for one bank account.</summary>
public sealed record BankReconciliationTotals(decimal BookBalance, decimal ClearedTotal, decimal UnclearedTotal)
{
    public decimal Difference => BookBalance - ClearedTotal - UnclearedTotal;
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
}

/// <summary>Soft-deleted draft counts per document table (compression preview).</summary>
public sealed record CompressCounts(int SalesDrafts, int PurchaseDrafts)
{
    public int Total => SalesDrafts + PurchaseDrafts;
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
