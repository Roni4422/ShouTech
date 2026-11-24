using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>
/// Selling-chain workflow: Quotation (QUO) → Sales Order (SO) → Delivery Note (DLV) → Sales Invoice (SAL).
/// Each step copies a posted source document into a new draft, supports partial quantities
/// keyed by SOURCE LINE (so duplicate-material lines convert independently), carries the
/// full source header, and records the chain link in document_links. Copied lines keep
/// the source line number in <see cref="CommercialLineDraft.SourceLineNo"/>, which the
/// repositories persist as the new document's line_no — the line identity every
/// progress and fulfilment computation matches on.
/// </summary>
public sealed class SalesChainService
{
    /// <summary>RBAC codes seeded by migration 098 (also in PermCat).</summary>
    public const string QuotationApprovePermission = "SALES.QUOTATION.APPROVE";
    public const string OrderApprovePermission = "SALES.ORDER.APPROVE";
    public const string InvoiceApprovePermission = "SALES.INVOICE.APPROVE";
    public const string QuotationDirectInvoicePermission = "SALES.QUOTATION.DIRECTINVOICE";

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
    /// When <paramref name="quantityBySourceLine"/> is null the full remaining quantity of
    /// each source line is copied; otherwise only the requested quantity of each keyed
    /// source line is copied (lines absent from the dictionary are not copied) and the
    /// previously fulfilled quantity of that same source line is respected.
    /// </summary>
    /// <returns>The database id of the created draft.</returns>
    public async Task<long> CreateFromSourceAsync(
        long sourceDocId,
        string docNo,
        DateOnly targetDate,
        string userId,
        IReadOnlyDictionary<int, decimal>? quantityBySourceLine = null,
        long? warehouseId = null,
        CancellationToken cancellationToken = default,
        decimal deliveryCost = 0m) =>
        await CreateFromSourceCoreAsync(sourceDocId, docNo, targetDate, userId,
            quantityBySourceLine, warehouseId, cancellationToken, deliveryCost, targetKindOverride: null).ConfigureAwait(false);

    /// <summary>
    /// AUTHORIZED SHORTCUT (W23, spec section 3): creates a SALES INVOICE
    /// draft directly from a posted quotation, skipping the order and
    /// delivery stages. Gated by SALES.QUOTATION.DIRECTINVOICE — without
    /// the permission the call is refused before any document is read for
    /// conversion. Line-keyed partial quantities and fulfilment tracking
    /// behave exactly like the staged chain conversions (the created
    /// invoice links QUO → SAL in document_links).
    /// </summary>
    public async Task<long> CreateInvoiceFromQuotationAsync(
        long quotationDocId,
        string docNo,
        DateOnly invoiceDate,
        string userId,
        Func<string, bool> hasPermission,
        IReadOnlyDictionary<int, decimal>? quantityBySourceLine = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hasPermission);
        if (!hasPermission(QuotationDirectInvoicePermission))
            throw new UnauthorizedAccessException(
                $"Direct quotation-to-invoice conversion requires the '{QuotationDirectInvoicePermission}' permission.");
        var source = await _repository.FindSalesByIdAsync(quotationDocId, cancellationToken).ConfigureAwait(false)
                     ?? throw new InvalidOperationException($"Source sales document '{quotationDocId}' was not found.");
        if (source.DocKind != CommercialDocKinds.SalesQuotation)
            throw new AppError(ErrorCodes.ChainTransitionNotAllowed,
                $"Documents cannot be converted from '{source.DocKind}' to '{CommercialDocKinds.SalesInvoice}'.",
                source.DocKind, CommercialDocKinds.SalesInvoice);
        return await CreateFromSourceCoreAsync(quotationDocId, docNo, invoiceDate, userId,
            quantityBySourceLine, null, cancellationToken, 0m,
            targetKindOverride: CommercialDocKinds.SalesInvoice).ConfigureAwait(false);
    }

    private async Task<long> CreateFromSourceCoreAsync(
        long sourceDocId,
        string docNo,
        DateOnly targetDate,
        string userId,
        IReadOnlyDictionary<int, decimal>? quantityBySourceLine,
        long? warehouseId,
        CancellationToken cancellationToken,
        decimal deliveryCost,
        string? targetKindOverride)
    {
        if (deliveryCost < 0m) throw new InvalidOperationException("Delivery cost cannot be negative.");
        if (sourceDocId <= 0) throw new ArgumentException("Source document is required.", nameof(sourceDocId));
        if (string.IsNullOrWhiteSpace(docNo)) throw new ArgumentException("Document number is required.", nameof(docNo));
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));

        var source = await _repository.FindSalesByIdAsync(sourceDocId, cancellationToken).ConfigureAwait(false)
                     ?? throw new InvalidOperationException($"Source sales document '{sourceDocId}' was not found.");

        if (source.Status != CommercialDocumentStatus.Posted)
            throw new AppError(ErrorCodes.ChainSourceNotPosted,
                $"The source document '{source.DocNo}' must be posted before it can be converted.",
                source.DocNo);

        string targetKind;
        if (targetKindOverride is not null)
        {
            // Authorized shortcut path (already permission-gated by the
            // caller): only QUO -> SAL is a sanctioned skip.
            if (source.DocKind != CommercialDocKinds.SalesQuotation
                || targetKindOverride != CommercialDocKinds.SalesInvoice)
                throw new AppError(ErrorCodes.ChainTransitionNotAllowed,
                    $"Documents cannot be converted from '{source.DocKind}' to '{targetKindOverride}'.",
                    source.DocKind, targetKindOverride);
            targetKind = targetKindOverride;
        }
        else if (!AllowedTransitions.TryGetValue(source.DocKind, out var mappedKind))
        {
            throw new AppError(ErrorCodes.ChainTransitionNotAllowed,
                $"Documents cannot be converted from '{source.DocKind}' to a later chain stage.",
                source.DocKind, string.Empty);
        }
        else
        {
            targetKind = mappedKind;
        }

        Dictionary<int, decimal>? requestedByLine = null;
        if (quantityBySourceLine is not null)
        {
            requestedByLine = new Dictionary<int, decimal>();
            var sourceLineNumbers = new HashSet<int>(source.Lines.Select(line => line.LineNo));
            foreach (var (sourceLineNo, quantity) in quantityBySourceLine)
            {
                if (sourceLineNo <= 0) throw new ArgumentOutOfRangeException(nameof(quantityBySourceLine), "Source line numbers must be positive.");
                if (quantity < 0m) throw new ArgumentOutOfRangeException(nameof(quantityBySourceLine), "Requested quantities cannot be negative.");
                if (!sourceLineNumbers.Contains(sourceLineNo))
                    throw new AppError(ErrorCodes.ChainQuantityExceedsRemaining,
                        $"The requested quantity for source line '{sourceLineNo}' exceeds the remaining quantity on the source document.",
                        sourceLineNo.ToString(System.Globalization.CultureInfo.InvariantCulture));
                requestedByLine.Add(sourceLineNo, quantity);
            }
        }

        var fulfilledByLine = await _repository.GetFulfilledQuantitiesBySourceLineAsync(
            source.CompanyId, sourceDocId, targetKind, cancellationToken).ConfigureAwait(false);

        var lines = new List<CommercialLineDraft>();
        decimal copiedSubtotal = 0m;
        foreach (var line in source.Lines)
        {
            var previouslyFulfilled = fulfilledByLine.GetValueOrDefault(line.LineNo);
            if (previouslyFulfilled > line.Qty)
                throw new AppError(ErrorCodes.ChainQuantityExceedsRemaining,
                    $"The requested quantity for '{line.Description}' exceeds the remaining quantity on the source document.",
                    line.Description);

            var remaining = line.Qty - previouslyFulfilled;
            if (remaining <= 0m)
                continue;

            var requested = remaining;
            if (requestedByLine is not null)
            {
                if (!requestedByLine.TryGetValue(line.LineNo, out requested))
                    continue;
                if (requested > remaining)
                    throw new AppError(ErrorCodes.ChainQuantityExceedsRemaining,
                        $"The requested quantity for '{line.Description}' exceeds the remaining quantity on the source document.",
                        line.Description);
            }

            if (requested <= 0m)
                continue;

            lines.Add(new CommercialLineDraft(
                line.MaterialId,
                line.MaterialCode,
                line.Description,
                line.Unit,
                requested,
                line.UnitPrice,
                line.DiscountPct,
                line.TaxRate,
                warehouseId ?? line.WarehouseId,
                line.LineNo));
            copiedSubtotal += decimal.Round(requested * line.UnitPrice * (1m - line.DiscountPct / 100m), 2);
        }

        if (lines.Count == 0)
            throw new AppError(ErrorCodes.ChainSourceFullyFulfilled,
                $"The source document '{source.DocNo}' is already fully fulfilled.",
                source.DocNo);

        // Header money follows the copied lines: the source header discount
        // and extra charges are carried pro-rata to the copied line subtotal
        // (a full conversion carries them in full), and the repository
        // recomputes the target's stored grand total from them.
        var carryRatio = source.Subtotal > 0m ? copiedSubtotal / source.Subtotal : 0m;
        var discountAmount = decimal.Round(source.DiscountAmount * carryRatio, 2);
        var extraAmount = decimal.Round(source.ExtraAmount * carryRatio, 2);

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
            source.Notes,
            lines,
            source.PartyCode,
            source.DeliveryDate,
            source.ValidTill,
            source.CostCenter,
            source.BranchName,
            source.SalesRepName,
            source.WarehouseName,
            discountAmount,
            extraAmount,
            // Delivery cost (W4) is entered per delivery note, never
            // carried from the source document.
            DeliveryCost: targetKind == CommercialDocKinds.DeliveryNote ? deliveryCost : 0m);

        return await _repository.CreateSalesDraftWithLinkAsync(
            draft, source.DocKind, sourceDocId, userId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates and posts a sales quotation draft; entry point of the chain.</summary>
    public async Task<long> CreateQuotationAsync(CommercialDocumentDraft draft, string userId, CancellationToken cancellationToken = default)
    {
        if (draft.DocKind != CommercialDocKinds.SalesQuotation)
            throw new AppError(ErrorCodes.ChainTransitionNotAllowed,
                $"Documents cannot be converted from '{draft.DocKind}' to '{CommercialDocKinds.SalesQuotation}'.",
                draft.DocKind, CommercialDocKinds.SalesQuotation);
        var docId = await _repository.CreateSalesDraftAsync(draft, userId, cancellationToken).ConfigureAwait(false);
        await _repository.PostSalesAsync(docId, userId, cancellationToken).ConfigureAwait(false);
        return docId;
    }

    /// <summary>Posts a chain draft (for example the sales order created from a quotation).</summary>
    public async Task PostAsync(long docId, string userId, CancellationToken cancellationToken = default) =>
        await _repository.PostSalesAsync(docId, userId, cancellationToken).ConfigureAwait(false);

    // ------------------------------------------- lifecycle (W23, migration 098)

    /// <summary>
    /// Amends a quotation/order as a new revision (prior version preserved
    /// in sau_document_revisions). See the repository contract for the
    /// state rules; SAL-7016 blocks invalid states, SAL-7017 a stale
    /// expected revision.
    /// </summary>
    public async Task<int> AmendAsync(
        long docId, CommercialDocumentDraft amended, int expectedRevision, string userId,
        string? reason = null, CancellationToken cancellationToken = default)
    {
        if (amended.DocKind is not (CommercialDocKinds.SalesQuotation or CommercialDocKinds.SalesOrder))
            throw new AppError(ErrorCodes.ApprovalStateInvalid,
                $"Only quotations and sales orders can be amended by revision; '{amended.DocKind}' cannot.",
                amended.DocNo, SalesApprovalState.NotRequired);
        return await _repository.AmendSalesDocumentAsync(docId, amended, expectedRevision, userId, reason, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The preserved prior revisions of a document (read-only).</summary>
    public Task<IReadOnlyList<DocumentRevisionSnapshot>> GetRevisionsAsync(long docId, CancellationToken cancellationToken = default) =>
        _repository.GetRevisionsAsync(docId, cancellationToken);

    /// <summary>The approval/amendment history of a document.</summary>
    public Task<IReadOnlyList<ApprovalHistoryEntry>> GetApprovalHistoryAsync(long docId, CancellationToken cancellationToken = default) =>
        _repository.GetApprovalHistoryAsync(docId, cancellationToken);

    /// <summary>Submits a draft QUO/SO/SAL document for approval.</summary>
    public Task SubmitForApprovalAsync(long docId, string userId, CancellationToken cancellationToken = default) =>
        _repository.SubmitForApprovalAsync(docId, userId, cancellationToken);

    /// <summary>
    /// Approves a Pending document. The caller must hold the kind's
    /// APPROVE permission (SALES.QUOTATION.APPROVE / SALES.ORDER.APPROVE /
    /// SALES.INVOICE.APPROVE, migration 098) — approval is a separated duty.
    /// </summary>
    public async Task ApproveAsync(long docId, string userId, Func<string, bool> hasPermission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hasPermission);
        var document = await _repository.FindSalesByIdAsync(docId, cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidOperationException($"Sales document '{docId}' was not found.");
        RequireApprovalPermission(document.DocKind, hasPermission);
        await _repository.ApproveDocumentAsync(docId, userId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rejects a Pending document; the reason is mandatory and stored in the history.</summary>
    public async Task RejectAsync(long docId, string userId, string reason, Func<string, bool> hasPermission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hasPermission);
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A rejection reason is required.", nameof(reason));
        var document = await _repository.FindSalesByIdAsync(docId, cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidOperationException($"Sales document '{docId}' was not found.");
        RequireApprovalPermission(document.DocKind, hasPermission);
        await _repository.RejectDocumentAsync(docId, userId, reason, cancellationToken).ConfigureAwait(false);
    }

    private static void RequireApprovalPermission(string docKind, Func<string, bool> hasPermission)
    {
        var permission = docKind switch
        {
            CommercialDocKinds.SalesQuotation => QuotationApprovePermission,
            CommercialDocKinds.SalesOrder => OrderApprovePermission,
            CommercialDocKinds.SalesInvoice => InvoiceApprovePermission,
            _ => throw new AppError(ErrorCodes.ApprovalStateInvalid,
                $"Documents of kind '{docKind}' do not use the sales approval workflow.",
                docKind, SalesApprovalState.NotRequired)
        };
        if (!hasPermission(permission))
            throw new UnauthorizedAccessException($"Approving this document requires the '{permission}' permission.");
    }

    /// <summary>True when the company's policy requires approval before posting (default OFF).</summary>
    public Task<bool> IsApprovalRequiredAsync(Guid companyId, CancellationToken cancellationToken = default) =>
        _repository.IsApprovalRequiredAsync(companyId, cancellationToken);

    /// <summary>Sets the company's approval policy flag.</summary>
    public Task SetApprovalRequiredAsync(Guid companyId, bool required, string userId, CancellationToken cancellationToken = default) =>
        _repository.SetApprovalRequiredAsync(companyId, required, userId, cancellationToken);
}
