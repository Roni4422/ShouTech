namespace ShouTech.Domain.Entities;

/// <summary>One GL line requested by a document posting, by account CODE.
/// The code is resolved against the canonical chart inside the posting
/// transaction; an unresolvable code fails the whole posting (ACC-5011).</summary>
public sealed record GlLineRequest(
    string AccountCode, decimal Debit, decimal Credit,
    string? CostCenter = null, string? Note = null);

/// <summary>A document posting request: header data plus balanced lines.
/// SourceDocType / SourceDocId / SourceDocNo link the created journal
/// entry back to its source document (migration 060).</summary>
public sealed record GlDocumentRequest(
    Guid CompanyId,
    DateOnly EntryDate,
    string Description,
    string SourceDocType,
    string SourceDocId,
    string SourceDocNo,
    string CurrencyCode,
    decimal ExchangeRate,
    IReadOnlyList<GlLineRequest> Lines);

/// <summary>Result of a document GL posting: the real journal entry.</summary>
public sealed record GlPostingResult(Guid JournalEntryId, string DocumentNumber)
{
    /// <summary>Budget warnings produced by BudgetGuard in Warn mode
    /// (Phase B 055); empty when no Active budget warned. A Stop-mode
    /// budget never reaches a result — it throws ACC-5018/5019.</summary>
    public IReadOnlyList<BudgetWarning> BudgetWarnings { get; init; } = Array.Empty<BudgetWarning>();
}

/// <summary>An authorized credit override for one posting (W1, migration
/// 095): the actor who holds SALES.CREDIT.OVERRIDE and the document
/// identity the credit_overrides audit row is recorded against. Lives in
/// Domain so repository contracts can carry it; PartyCreditGuard (Infra)
/// consumes it.</summary>
public sealed record CreditOverrideContext(string Actor, string? DocKind, string? DocNo);
