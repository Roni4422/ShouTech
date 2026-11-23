using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>
/// Buying-chain workflow (VIBE modern spec, milestone M1): Request for
/// Quotation (RFQ) -> Purchase Order (PO) -> Purchase Receipt (RCV) ->
/// Purchase Invoice (PUR), with ERPNext-style Purchase Returns (PRT)
/// from a posted receipt or invoice. The buying twin of
/// SalesChainService, on the same commercial document records and the
/// same document_links table: each step copies a posted source document
/// into a new draft of the next kind, supports partial quantities, and
/// records the chain link. Clean-room re-implementation of the ERPNext /
/// Al-Ameen 10 buying flow (concepts only).
/// </summary>
public sealed class BuyingChainService
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> AllowedTransitions = new Dictionary<string, IReadOnlyList<string>>
    {
        [CommercialDocKinds.RequestForQuotation] = new[] { CommercialDocKinds.PurchaseOrder },
        [CommercialDocKinds.PurchaseOrder] = new[] { CommercialDocKinds.PurchaseReceipt },
        [CommercialDocKinds.PurchaseReceipt] = new[] { CommercialDocKinds.PurchaseInvoice, CommercialDocKinds.PurchaseReturn },
        [CommercialDocKinds.PurchaseInvoice] = new[] { CommercialDocKinds.PurchaseReturn }
    };

    private readonly IPurchaseDocumentRepository _repository;

    public BuyingChainService(IPurchaseDocumentRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>
    /// Creates a new draft document of the next chain stage from a posted source document.
    /// When <paramref name="quantityByMaterialId"/> is null the full remaining quantity of each
    /// line is copied; otherwise only the requested quantities are copied and the sum of
    /// previously fulfilled quantities is respected. A receipt defaults to creating a
    /// purchase invoice; an invoice creates a purchase return; pass an explicit
    /// <paramref name="targetDocKind"/> to branch otherwise (for example a purchase return
    /// from a receipt).
    /// </summary>
    /// <returns>The database id of the created draft.</returns>
    public async Task<long> CreateFromSourceAsync(
        long sourceDocId,
        string docNo,
        DateOnly targetDate,
        string userId,
        IReadOnlyDictionary<long, decimal>? quantityByMaterialId = null,
        long? warehouseId = null,
        CancellationToken cancellationToken = default,
        string? targetDocKind = null)
    {
        if (sourceDocId <= 0) throw new ArgumentException("Source document is required.", nameof(sourceDocId));
        if (string.IsNullOrWhiteSpace(docNo)) throw new ArgumentException("Document number is required.", nameof(docNo));
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));

        var source = await _repository.FindPurchaseByIdAsync(sourceDocId, cancellationToken).ConfigureAwait(false)
                     ?? throw new InvalidOperationException($"Source purchase document '{sourceDocId}' was not found.");

        if (source.Status != CommercialDocumentStatus.Posted)
            throw new InvalidOperationException($"Only posted documents can continue the chain; document '{source.DocNo}' is '{source.Status}'.");

        if (!AllowedTransitions.TryGetValue(source.DocKind, out var targetKinds))
            throw new InvalidOperationException($"Document kind '{source.DocKind}' cannot continue the buying chain.");

        var resolvedTargetKind = string.IsNullOrWhiteSpace(targetDocKind)
            ? source.DocKind == CommercialDocKinds.PurchaseReceipt
                ? CommercialDocKinds.PurchaseInvoice
                : targetKinds[0]
            : targetKinds.Contains(targetDocKind)
                ? targetDocKind
                : throw new InvalidOperationException(
                    $"Document kind '{source.DocKind}' cannot continue the buying chain into '{targetDocKind}'.");

        var fulfilled = await _repository.GetPurchaseFulfilledQuantitiesAsync(source.CompanyId, sourceDocId, resolvedTargetKind, cancellationToken).ConfigureAwait(false);

        var lines = new List<CommercialLineDraft>();
        foreach (var line in source.Lines)
        {
            var fulfilledKey = line.MaterialId ?? -line.LineNo;
            var alreadyFulfilled = fulfilled.TryGetValue(fulfilledKey, out var value) ? value : 0m;
            var remaining = line.Qty - alreadyFulfilled;
            if (remaining <= 0m)
                continue;

            var requested = quantityByMaterialId is not null && line.MaterialId is not null && quantityByMaterialId.TryGetValue(line.MaterialId.Value, out var overrideQty)
                ? overrideQty
                : remaining;
            if (requested <= 0m)
                continue;
            if (requested > remaining)
                throw new InvalidOperationException(
                    $"Requested quantity {requested} for '{line.Description}' exceeds the remaining quantity {remaining} of source document '{source.DocNo}'.");

            lines.Add(new CommercialLineDraft(
                line.MaterialId,
                line.MaterialCode,
                line.Description,
                line.Unit,
                requested,
                line.UnitPrice,
                line.DiscountPct,
                line.TaxRate,
                warehouseId ?? line.WarehouseId));
        }

        if (lines.Count == 0)
            throw new InvalidOperationException($"Source document '{source.DocNo}' is already fully fulfilled.");

        var draft = new CommercialDocumentDraft(
            source.CompanyId,
            resolvedTargetKind,
            docNo.Trim(),
            targetDate,
            source.PartyId,
            source.PartyName,
            warehouseId ?? source.WarehouseId,
            source.CurrencyCode,
            source.ExchangeRate,
            $"From {source.DocKind} {source.DocNo}",
            lines);

        var targetDocId = await _repository.CreatePurchaseDraftAsync(draft, userId, cancellationToken).ConfigureAwait(false);
        await _repository.AddLinkAsync(source.CompanyId, source.DocKind, sourceDocId, resolvedTargetKind, targetDocId, userId, cancellationToken).ConfigureAwait(false);
        return targetDocId;
    }

    /// <summary>Creates and posts a request-for-quotation draft; entry point of the buying chain.</summary>
    public async Task<long> CreateRequestForQuotationAsync(CommercialDocumentDraft draft, string userId, CancellationToken cancellationToken = default)
    {
        if (draft.DocKind != CommercialDocKinds.RequestForQuotation)
            throw new InvalidOperationException("This method only creates purchase requests for quotation.");
        var docId = await _repository.CreatePurchaseDraftAsync(draft, userId, cancellationToken).ConfigureAwait(false);
        await _repository.PostPurchaseAsync(docId, userId, cancellationToken).ConfigureAwait(false);
        return docId;
    }

    /// <summary>Posts a buying-chain draft (for example the purchase order created from an RFQ).</summary>
    public async Task PostAsync(long docId, string userId, CancellationToken cancellationToken = default) =>
        await _repository.PostPurchaseAsync(docId, userId, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Creates a purchase-return (PRT) draft from a posted purchase receipt or
    /// purchase invoice, limited to that document's not-yet-returned quantities.
    /// Posting it reverses the receipt's stock and the invoice's supplier
    /// liability through the same side-effect contract.
    /// </summary>
    /// <returns>The database id of the created return draft.</returns>
    public Task<long> CreateReturnFromSourceAsync(
        long sourceDocId,
        string docNo,
        DateOnly targetDate,
        string userId,
        IReadOnlyDictionary<long, decimal>? quantityByMaterialId = null,
        long? warehouseId = null,
        CancellationToken cancellationToken = default)
        => CreateFromSourceAsync(sourceDocId, docNo, targetDate, userId, quantityByMaterialId,
            warehouseId, cancellationToken, CommercialDocKinds.PurchaseReturn);
}
