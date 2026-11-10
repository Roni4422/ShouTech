using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>
/// Selling-chain workflow: Quotation (QUO) → Sales Order (SO) → Delivery Note (DLV) → Sales Invoice (SAL).
/// Each step copies a posted source document into a new draft, supports partial quantities,
/// and records the chain link in document_links.
/// </summary>
public sealed class SalesChainService
{
    private static readonly IReadOnlyDictionary<string, string> AllowedTransitions = new Dictionary<string, string>
    {
        [CommercialDocKinds.SalesQuotation] = CommercialDocKinds.SalesOrder,
        [CommercialDocKinds.SalesOrder] = CommercialDocKinds.DeliveryNote,
        [CommercialDocKinds.DeliveryNote] = CommercialDocKinds.SalesInvoice
    };

    private readonly ICommercialDocumentRepository _repository;

    public SalesChainService(ICommercialDocumentRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>
    /// Creates a new draft document of the next chain stage from a posted source document.
    /// When <paramref name="quantityByMaterialId"/> is null the full remaining quantity of each
    /// line is copied; otherwise only the requested quantities are copied and the sum of
    /// previously fulfilled quantities is respected.
    /// </summary>
    /// <returns>The database id of the created draft.</returns>
    public async Task<long> CreateFromSourceAsync(
        long sourceDocId,
        string docNo,
        DateOnly targetDate,
        string userId,
        IReadOnlyDictionary<long, decimal>? quantityByMaterialId = null,
        long? warehouseId = null,
        CancellationToken cancellationToken = default)
    {
        if (sourceDocId <= 0) throw new ArgumentException("Source document is required.", nameof(sourceDocId));
        if (string.IsNullOrWhiteSpace(docNo)) throw new ArgumentException("Document number is required.", nameof(docNo));
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));

        var source = await _repository.FindSalesByIdAsync(sourceDocId, cancellationToken).ConfigureAwait(false)
                     ?? throw new InvalidOperationException($"Source sales document '{sourceDocId}' was not found.");

        if (source.Status != CommercialDocumentStatus.Posted)
            throw new InvalidOperationException($"Only posted documents can continue the chain; document '{source.DocNo}' is '{source.Status}'.");

        if (!AllowedTransitions.TryGetValue(source.DocKind, out var targetKind))
            throw new InvalidOperationException($"Document kind '{source.DocKind}' cannot continue the selling chain.");

        var fulfilled = await _repository.GetFulfilledQuantitiesAsync(source.CompanyId, sourceDocId, targetKind, cancellationToken).ConfigureAwait(false);

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
            targetKind,
            docNo.Trim(),
            targetDate,
            source.PartyId,
            source.PartyName,
            warehouseId ?? source.WarehouseId,
            source.CurrencyCode,
            source.ExchangeRate,
            $"From {source.DocKind} {source.DocNo}",
            lines);

        var targetDocId = await _repository.CreateSalesDraftAsync(draft, userId, cancellationToken).ConfigureAwait(false);
        await _repository.AddLinkAsync(source.CompanyId, source.DocKind, sourceDocId, targetKind, targetDocId, userId, cancellationToken).ConfigureAwait(false);
        return targetDocId;
    }

    /// <summary>Creates and posts a sales quotation draft; entry point of the chain.</summary>
    public async Task<long> CreateQuotationAsync(CommercialDocumentDraft draft, string userId, CancellationToken cancellationToken = default)
    {
        if (draft.DocKind != CommercialDocKinds.SalesQuotation)
            throw new InvalidOperationException("This method only creates sales quotations.");
        var docId = await _repository.CreateSalesDraftAsync(draft, userId, cancellationToken).ConfigureAwait(false);
        await _repository.PostSalesAsync(docId, userId, cancellationToken).ConfigureAwait(false);
        return docId;
    }

    /// <summary>Posts a chain draft (for example the sales order created from a quotation).</summary>
    public async Task PostAsync(long docId, string userId, CancellationToken cancellationToken = default) =>
        await _repository.PostSalesAsync(docId, userId, cancellationToken).ConfigureAwait(false);
}
