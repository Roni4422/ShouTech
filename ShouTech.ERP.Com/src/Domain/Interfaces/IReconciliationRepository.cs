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
    /// W8 (migration 103): <paramref name="discountAmount"/> is a settlement
    /// discount in DOCUMENT currency (settles the document alongside the
    /// payment; posts Dr settlement discount / Cr party control in the
    /// allocation journal). <paramref name="docAmount"/> is the amount
    /// settled on the document in DOCUMENT currency for cross-currency
    /// allocations (<paramref name="amount"/> always stays the
    /// payment-currency amount consumed from the payment); it defaults to
    /// <paramref name="amount"/> and must equal it when the currencies
    /// match. The realized FX difference posts in the same journal.
    /// </summary>
    Task<Guid> AllocateAsync(string companyId, string partyKind, long paymentLedgerId, string docTable, long docId, decimal amount, string userId, string? note = null, decimal discountAmount = 0m, decimal? docAmount = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverses ONE advance allocation (migration 095) in one transaction:
    /// mirrors its clearing journal, releases the document's paid_amount,
    /// writes a migration-079 audit row, and deletes the allocation —
    /// restoring the advance's available balance. Advance allocations
    /// only; returns the clearing entry's reversal journal id.
    /// </summary>
    Task<Guid> UnallocateAsync(string companyId, Guid allocationId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Reverses the most recent advance allocation between one
    /// payment/advance entry and one document (the PayRecWin action).</summary>
    Task<Guid> UnallocateLatestAdvanceAsync(string companyId, long paymentLedgerId, string docTable, long docId, string userId, CancellationToken cancellationToken = default);
}
