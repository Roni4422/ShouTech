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

    public Task<Guid> AllocateAsync(string companyId, string partyKind, long paymentLedgerId, string docTable, long docId, decimal amount, string userId, string? note = null, CancellationToken cancellationToken = default)
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
        if (string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException("A current user is required for audited writes.");
        return _repository.AllocateAsync(companyId, partyKind, paymentLedgerId, docTable, docId, amount, userId, note, cancellationToken);
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
