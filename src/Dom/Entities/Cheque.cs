namespace ShouTech.Domain.Entities;

/// <summary>Cheque direction: IN = received into the portfolio, OUT = issued.</summary>
public static class ChequeDirections
{
    public const string In = "IN";
    public const string Out = "OUT";

    public static readonly IReadOnlyList<string> All = new[] { In, Out };
}

/// <summary>
/// Cheque lifecycle statuses (RS-4). Incoming cheques start InPortfolio;
/// outgoing cheques start Issued. Terminal: Cleared, Bounced, Endorsed,
/// Cancelled. Legal transitions are enforced by ChequeService.
/// </summary>
public static class ChequeStatuses
{
    public const string InPortfolio = "InPortfolio";
    public const string Issued = "Issued";
    public const string Deposited = "Deposited";
    public const string Cleared = "Cleared";
    public const string Bounced = "Bounced";
    public const string Endorsed = "Endorsed";
    public const string Cancelled = "Cancelled";

    public static readonly IReadOnlyList<string> All =
        new[] { InPortfolio, Issued, Deposited, Cleared, Bounced, Endorsed, Cancelled };
}

/// <summary>One cheque in the company portfolio / issued-cheque register.</summary>
public sealed class Cheque
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Direction { get; set; } = ChequeDirections.In;
    public string ChequeNo { get; set; } = string.Empty;
    public string BankName { get; set; } = string.Empty;
    public string? BranchName { get; set; }
    public string? DrawerName { get; set; }
    public string? PartyAccountCode { get; set; }
    public string? PartyName { get; set; }
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = "SAR";
    public decimal ExchangeRate { get; set; } = 1m;
    public DateOnly? IssueDate { get; set; }
    public DateOnly DueDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public string Status { get; set; } = ChequeStatuses.InPortfolio;
    public string? PortfolioAccountCode { get; set; }
    public string? DepositBankAccountCode { get; set; }
    public DateOnly? DepositDate { get; set; }
    public DateOnly? ClearanceDate { get; set; }
    public string? EndorsedToAccountCode { get; set; }
    public DateOnly? BounceDate { get; set; }
    public string? BounceReason { get; set; }
    public decimal FeesAmount { get; set; }
    /// <summary>The cheque's OWN issue journal entry (Step 8: one entry
    /// per cheque, linked via migration-060 source columns with
    /// source_doc_type CHQ-ISSUE and source_doc_id = this cheque's id).
    /// Pre-Step-8 rows may still carry a batch entry shared with sibling
    /// cheques; ChequeService detects that sharing before reversing.</summary>
    public Guid? JournalEntryId { get; set; }
    public string? Notes { get; set; }
    /// <summary>The batch voucher number shown in ChqWin (migration 063;
    /// CHQ-05: previously displayed but never persisted).</summary>
    public string? BatchDocNo { get; set; }
    /// <summary>Stop-payment note recorded when an outgoing cheque is
    /// cancelled (migration 063).</summary>
    public string? CancelReason { get; set; }
}

/// <summary>Journal source-document types for cheque postings (migration
/// 060 link columns). One live entry per (type, cheque id).</summary>
public static class ChequeSourceTypes
{
    public const string Issue = "CHQ-ISSUE";
    public const string Clear = "CHQ-CLEAR";
    public const string Bounce = "CHQ-BOUNCE";
    public const string Endorse = "CHQ-ENDORSE";
    public const string Cancel = "CHQ-CANCEL";
    public const string Fee = "CHQ-FEE";
}
