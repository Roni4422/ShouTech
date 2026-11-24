using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>
/// Payment Reconciliation application service. Validation here is the first
/// line of defence; the repository re-reads and re-validates every amount
/// inside the allocation transaction before writing.
/// </summary>
public sealed class ReconciliationService
{
    private readonly IReconciliationRepository _repository;

    public ReconciliationService(IReconciliationRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public Task<IReadOnlyList<OpenDocumentRow>> GetOpenDocumentsAsync(string companyId, string partyKind, CancellationToken cancellationToken = default)
    {
        RequirePartyKind(partyKind);
        return _repository.GetOpenDocumentsAsync(RequireCompany(companyId), partyKind, cancellationToken);
    }

    public Task<IReadOnlyList<UnallocatedPaymentRow>> GetPaymentsAsync(string companyId, string partyKind, CancellationToken cancellationToken = default)
    {
        RequirePartyKind(partyKind);
        return _repository.GetPaymentsAsync(RequireCompany(companyId), partyKind, cancellationToken);
    }

    public Task<Guid> AllocateAsync(string companyId, string partyKind, long paymentLedgerId, string docTable, long docId, decimal amount, string userId, string? note = null, decimal discountAmount = 0m, decimal? docAmount = null, bool settlementDiscountAuthorized = false, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        RequirePartyKind(partyKind);
        if (paymentLedgerId <= 0)
            throw new ArgumentException("A payment entry is required.", nameof(paymentLedgerId));
        if (docId <= 0)
            throw new ArgumentException("A document is required.", nameof(docId));
        if (docTable is not ("SALES" or "PURCHASE"))
            throw new ArgumentException("Document table must be SALES or PURCHASE.", nameof(docTable));
        if (amount <= 0)
            throw new InvalidOperationException("Allocation amount must be greater than zero.");
        if (discountAmount < 0)
            throw new InvalidOperationException("Settlement discount cannot be negative.");
        // W8 backstop: the window checks SALES.SETTLEMENT.DISCOUNT via
        // the audited PermServ.Require (which logs the denial); a direct
        // service caller with a discount but no authorization is refused
        // here too, so the discount can never slip in unaudited.
        if (discountAmount > 0 && !settlementDiscountAuthorized)
            throw new InvalidOperationException(
                "A settlement discount requires the SALES.SETTLEMENT.DISCOUNT permission.");
        if (docAmount is <= 0)
            throw new InvalidOperationException("Document allocation amount must be greater than zero.");
        if (string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException("A current user is required for audited writes.");
        return _repository.AllocateAsync(companyId, partyKind, paymentLedgerId, docTable, docId, amount, userId, note, discountAmount, docAmount, cancellationToken);
    }

    /// <summary>
    /// W8 FIFO suggestion: distributes a payment amount across one
    /// party's open documents, oldest due first (due_date, falling back
    /// to doc_date), each line capped at the document's open amount.
    /// Same-currency documents only — a cross-currency line needs a
    /// document-currency amount the suggestion cannot price without a
    /// rate decision, so those stay manual single allocations. Pure
    /// computation over the caller's already-loaded rows: the window
    /// shows the plan for editing and saves through AllocateAsync.
    /// </summary>
    public static IReadOnlyList<(OpenDocumentRow Document, decimal Amount)> SuggestFifo(
        IEnumerable<OpenDocumentRow> documents, long partyId, string paymentCurrency, decimal paymentAvailable)
    {
        var plan = new List<(OpenDocumentRow, decimal)>();
        if (paymentAvailable <= 0)
            return plan;
        var remaining = paymentAvailable;
        foreach (var doc in documents
            .Where(d => d.PartyId == partyId
                && d.OpenAmount > 0.005m
                && string.Equals(d.CurrencyCode.Trim(), paymentCurrency.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.FifoDate)
            .ThenBy(d => d.DocId))
        {
            if (remaining <= 0.005m)
                break;
            var take = Math.Min(doc.OpenAmount, remaining);
            plan.Add((doc, take));
            remaining -= take;
        }
        return plan;
    }

    private static string RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
        return companyId;
    }

    private static void RequirePartyKind(string partyKind)
    {
        if (partyKind is not ("C" or "S"))
            throw new ArgumentException("Party kind must be 'C' (customer) or 'S' (supplier).", nameof(partyKind));
    }
}
