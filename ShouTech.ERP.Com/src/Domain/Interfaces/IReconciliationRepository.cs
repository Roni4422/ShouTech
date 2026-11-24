using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

public interface IReconciliationRepository
{
    /// <summary>partyKind: "C" = customers (sales documents), "S" = suppliers (purchase documents).</summary>
    Task<IReadOnlyList<OpenDocumentRow>> GetOpenDocumentsAsync(string companyId, string partyKind, CancellationToken cancellationToken = default);

    /// <summary>partyKind: "C" = customers (credit-side ledger entries), "S" = suppliers (debit-side entries).
    /// For customers the list also carries posted credit notes (SRT, G17) at their net credit
    /// (gross credit minus refunds already paid out on the same return document).</summary>
    Task<IReadOnlyList<UnallocatedPaymentRow>> GetPaymentsAsync(string companyId, string partyKind, CancellationToken cancellationToken = default);

    /// <summary>
    /// Allocates part of a payment to an open document in ONE transaction:
    /// the payment, its prior allocations, and the document's open amount are
    /// re-read and re-validated inside the transaction before inserting the
    /// allocation and increasing the document's paid_amount.
    /// Returns the new allocation ID.
    /// </summary>
    Task<Guid> AllocateAsync(string companyId, string partyKind, long paymentLedgerId, string docTable, long docId, decimal amount, string userId, string? note = null, CancellationToken cancellationToken = default);
}
