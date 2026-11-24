namespace ShouTech.Domain.Entities;

/// <summary>Lead lifecycle (migration 057, design section 4.2). A lead is
/// its own entity (decision C1); Converted is terminal and is stamped by
/// the conversion transaction together with the party / opportunity
/// links. Unqualified is the dead-end for a lead that will not convert.</summary>
public static class CrmLeadStatuses
{
    public const string New = "New";
    public const string Working = "Working";
    public const string Qualified = "Qualified";
    public const string Converted = "Converted";
    public const string Unqualified = "Unqualified";

    public static readonly IReadOnlyList<string> All = new[] { New, Working, Qualified, Converted, Unqualified };
}

/// <summary>Lead / opportunity sources (design section 4.2 seeded
/// vocabulary; stored as free text so a company can extend it).</summary>
public static class CrmSources
{
    public const string Referral = "Referral";
    public const string Web = "Web";
    public const string Exhibition = "Exhibition";
    public const string ColdCall = "Cold Call";
    public const string ExistingCustomer = "Existing Customer";
    public const string Other = "Other";

    public static readonly IReadOnlyList<string> All =
        new[] { Referral, Web, Exhibition, ColdCall, ExistingCustomer, Other };
}

/// <summary>Stage kinds. An opportunity's status is DERIVED from its
/// stage's type (design section 4.2) — there is no second status field
/// on the opportunity to disagree with the board.</summary>
public static class CrmStageTypes
{
    public const string Open = "Open";
    public const string Won = "Won";
    public const string Lost = "Lost";

    public static readonly IReadOnlyList<string> All = new[] { Open, Won, Lost };
}

/// <summary>Opportunity types (design section 4.2).</summary>
public static class CrmOpportunityTypes
{
    public const string NewBusiness = "New Business";
    public const string ExistingBusiness = "Existing Business";
    public const string Upsell = "Upsell";

    public static readonly IReadOnlyList<string> All = new[] { NewBusiness, ExistingBusiness, Upsell };
}

/// <summary>Activity kinds (design section 4.2: calls / meetings /
/// emails / tasks).</summary>
public static class CrmActivityTypes
{
    public const string Call = "Call";
    public const string Meeting = "Meeting";
    public const string Email = "Email";
    public const string Task = "Task";

    public static readonly IReadOnlyList<string> All = new[] { Call, Meeting, Email, Task };
}

/// <summary>Activity lifecycle. Overdue is NOT a stored status: it is
/// derived (Planned + due date in the past) so the worklist can never
/// disagree with the calendar.</summary>
public static class CrmActivityStatuses
{
    public const string Planned = "Planned";
    public const string Done = "Done";
    public const string Cancelled = "Cancelled";

    public static readonly IReadOnlyList<string> All = new[] { Planned, Done, Cancelled };
}

/// <summary>Link kinds in crm_opportunity_documents (design section 4.2).</summary>
public static class CrmDocumentLinkTypes
{
    public const string Quotation = "Quotation";
    public const string Order = "Order";
    public const string Invoice = "Invoice";

    public static readonly IReadOnlyList<string> All = new[] { Quotation, Order, Invoice };

    /// <summary>The sales_documents doc_kind a link type points at.</summary>
    public static string DocKindFor(string linkType) => linkType switch
    {
        Quotation => "QUO",
        Order => "SO",
        Invoice => "SAL",
        _ => throw new ArgumentOutOfRangeException(nameof(linkType), linkType, "Unknown CRM document link type.")
    };
}

/// <summary>One configurable pipeline stage (Odoo pattern: stages are
/// configuration, not code). Probability is a percentage 0-100 and is
/// the default an opportunity takes when it ENTERS the stage; the
/// opportunity may then override it.</summary>
public sealed class CrmStage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public int Sequence { get; set; }
    public decimal Probability { get; set; }
    public string StageType { get; set; } = CrmStageTypes.Open;
    public bool IsActive { get; set; } = true;
}

/// <summary>One lost reason (seeded per design section 4.2; required on
/// an opportunity that moves into a Lost stage).</summary>
public sealed class CrmLostReason
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

/// <summary>One lead (decision C1: its own entity, never a party until
/// conversion). ConvertedPartyId / ConvertedOpportunityId are stamped
/// once by conversion and are the lead's only links into core data.</summary>
public sealed class CrmLead
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string LeadName { get; set; } = string.Empty;
    public string? Organization { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Mobile { get; set; }
    public string? Address { get; set; }
    public string? Source { get; set; }
    public string? Campaign { get; set; }
    public string Status { get; set; } = CrmLeadStatuses.New;
    /// <summary>CORE sales rep code (sales_reps, migration 031) — reps
    /// stay core; CRM only references them.</summary>
    public string? AssignedRepCode { get; set; }
    public Guid? ConvertedPartyId { get; set; }
    public Guid? ConvertedOpportunityId { get; set; }
    public string? Notes { get; set; }
}

/// <summary>One opportunity. PartyId points at the unified master
/// (parties.id); LeadId keeps the origin lead after conversion.
/// Probability is a percentage 0-100; the weighted (expected) value is
/// ExpectedAmount x Probability / 100 — the pipeline math the board
/// footers and the proof harness pin.</summary>
public sealed class CrmOpportunity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public Guid? PartyId { get; set; }
    /// <summary>Display snapshot resolved by the repository (party name).</summary>
    public string? PartyName { get; set; }
    /// <summary>The party's operational integer identity (parties.ledger_id).</summary>
    public long? PartyLedgerId { get; set; }
    public Guid? LeadId { get; set; }
    public Guid? StageId { get; set; }
    /// <summary>Display snapshot resolved by the repository.</summary>
    public string? StageName { get; set; }
    /// <summary>Display snapshot: the stage's type (Open / Won / Lost) —
    /// the opportunity's derived status.</summary>
    public string? StageType { get; set; }
    public decimal Probability { get; set; }
    public decimal ExpectedAmount { get; set; }
    public string CurrencyCode { get; set; } = "SAR";
    public string? ExpectedCloseDate { get; set; }
    public string? OppType { get; set; }
    public string? Source { get; set; }
    public string? Campaign { get; set; }
    public string? AssignedRepCode { get; set; }
    public Guid? LostReasonId { get; set; }
    public string? LostNotes { get; set; }

    /// <summary>The opportunity's priced lines (migration 092). Loaded
    /// with the opportunity and saved in the opportunity's save
    /// transaction. When the list is non-empty, ExpectedAmount IS the
    /// items total (the service/repository keep them in step).</summary>
    public List<CrmOpportunityItem> Items { get; set; } = new();

    /// <summary>Weighted pipeline value: amount x probability.</summary>
    public decimal WeightedValue => decimal.Round(ExpectedAmount * Probability / 100m, 4, MidpointRounding.AwayFromZero);
}

/// <summary>One priced line of an opportunity (migration 092,
/// crm_opportunity_items). Mirrors sales_document_lines conventions:
/// LineSubtotal is NET of the line discount, LineTax is computed on
/// the net subtotal, LineTotal = subtotal + tax — all rounded to 4dp
/// AwayFromZero, the CRM/quotation rounding path. MaterialId is the
/// materials table's integer identity and is nullable: a free-text
/// line is legitimate before a material is chosen.</summary>
public sealed class CrmOpportunityItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OppId { get; set; }
    public int LineNo { get; set; }
    public long? MaterialId { get; set; }
    public string? MaterialCode { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Qty { get; set; }
    public string? Unit { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPct { get; set; }
    public decimal TaxRate { get; set; }

    public decimal LineSubtotal => decimal.Round(Qty * UnitPrice * (1m - DiscountPct / 100m), 4, MidpointRounding.AwayFromZero);
    public decimal LineTax => decimal.Round(LineSubtotal * TaxRate / 100m, 4, MidpointRounding.AwayFromZero);
    public decimal LineTotal => LineSubtotal + LineTax;
}

/// <summary>A duplicate found for a lead (sales cycle upgrade B1, gap
/// G1): the same email or phone already exists on another lead or on
/// a party in the unified master. SourceKind is "Lead" or "Party";
/// MatchedField is "Email", "Phone" or "Mobile". The CRM-9007 error
/// message names this match so the user can link instead of
/// duplicating.</summary>
public sealed record CrmDuplicateMatch(
    string SourceKind, Guid MatchId, string DisplayName, string MatchedField, string MatchedValue)
{
    public string Describe() => $"{SourceKind} '{DisplayName}' matches on {MatchedField} '{MatchedValue}'";
}

/// <summary>One recorded stage change (mandate: pipeline movement is
/// auditable). Written by every stage move AND by opportunity creation
/// (from-stage null), inside the same transaction as the move.</summary>
public sealed class CrmStageHistoryEntry
{
    public Guid Id { get; set; }
    public Guid OppId { get; set; }
    public Guid? FromStageId { get; set; }
    public Guid? ToStageId { get; set; }
    public string? FromStageName { get; set; }
    public string? ToStageName { get; set; }
    public decimal? FromProbability { get; set; }
    public decimal? ToProbability { get; set; }
    public string? ChangedBy { get; set; }
    public string? ChangedAt { get; set; }
    public string? Notes { get; set; }
}

/// <summary>One opportunity &lt;-&gt; document link (crm_opportunity_documents),
/// with the document's live header facts resolved from sales_documents
/// so the documents tab never shows a stale copy.</summary>
public sealed class CrmOpportunityDocument
{
    public Guid Id { get; set; }
    public Guid OppId { get; set; }
    public string DocKind { get; set; } = string.Empty;
    public long DocId { get; set; }
    public string LinkType { get; set; } = CrmDocumentLinkTypes.Quotation;
    /// <summary>Live from sales_documents (null when the row is gone).</summary>
    public string? DocNo { get; set; }
    public string? DocStatus { get; set; }
    public decimal? DocTotal { get; set; }
    public string? CreatedAt { get; set; }
}

/// <summary>A line for a CRM-created quotation (the caller passes real
/// material lines when it has them; with no lines the repository
/// builds the lines from the opportunity's items — migration 092 —
/// and only an item-less opportunity falls back to one line from its
/// title + expected amount, which the user then edits in InvoiceWin).
/// DiscountPct / TaxRate default to 0 for legacy callers.</summary>
public sealed record CrmQuotationLine(
    long? MaterialId, string? MaterialCode, string Description,
    string? Unit, decimal Qty, decimal UnitPrice,
    decimal DiscountPct = 0m, decimal TaxRate = 0m);

/// <summary>The quotation a CRM create produced: the sales_documents id
/// + number of the Quotation-kind draft.</summary>
public sealed record CrmQuotationResult(long DocId, string DocNo);

/// <summary>The outcome of a lead conversion: the party created in the
/// unified master (Guid id + operational ledger id + assigned code) and
/// the opportunity created at the first Open stage.</summary>
public sealed record CrmConversionResult(
    Guid PartyId, long PartyLedgerId, string PartyCode, Guid OpportunityId);

/// <summary>One follow-up activity against exactly one of lead /
/// opportunity / party (enforced by the service + repository).</summary>
public sealed class CrmActivity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Subject { get; set; } = string.Empty;
    public string ActivityType { get; set; } = CrmActivityTypes.Task;
    /// <summary>Due timestamp text ('yyyy-MM-dd HH:mm' or a plain date).</summary>
    public string DueAt { get; set; } = string.Empty;
    public string? CompletedAt { get; set; }
    public string Status { get; set; } = CrmActivityStatuses.Planned;
    public Guid? LeadId { get; set; }
    public Guid? OppId { get; set; }
    public Guid? PartyId { get; set; }
    public string? AssignedTo { get; set; }
    public string? ResultNotes { get; set; }

    /// <summary>Derived overdue flag for the worklist: still Planned and
    /// the due moment is before <paramref name="asOf"/> (compared on the
    /// date part when the stored value carries no time).</summary>
    public bool IsOverdue(string asOf)
        => Status == CrmActivityStatuses.Planned
           && string.CompareOrdinal(DueAt, asOf) < 0;
}

/// <summary>One pipeline-board column: a stage plus its opportunities
/// and the column totals (raw + weighted) the board footer shows.</summary>
public sealed record CrmPipelineColumn(
    CrmStage Stage, IReadOnlyList<CrmOpportunity> Opportunities)
{
    public decimal TotalAmount => Opportunities.Sum(o => o.ExpectedAmount);
    public decimal WeightedAmount => Opportunities.Sum(o => o.WeightedValue);
}

/// <summary>A party as the CRM pickers offer it (unified master row).</summary>
public sealed record CrmPartyOption(Guid Id, string Code, string Name, long LedgerId);

/// <summary>A CORE sales rep as the CRM pickers offer it.</summary>
public sealed record CrmRepOption(string Code, string Name);

/// <summary>The whole board for one company: one column per stage in
/// sequence order, plus the open-pipeline weighted total.</summary>
public sealed record CrmPipelineBoard(IReadOnlyList<CrmPipelineColumn> Columns)
{
    public decimal OpenWeightedTotal => Columns
        .Where(c => c.Stage.StageType == CrmStageTypes.Open)
        .Sum(c => c.WeightedAmount);
}
