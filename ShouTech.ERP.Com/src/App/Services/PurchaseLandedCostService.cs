using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>
/// Authorization boundary for purchase landed-cost drafts, posting and
/// reversal. Every operation rechecks the current user's permission.
/// </summary>
public sealed class PurchaseLandedCostService
{
    private readonly IPurchaseLandedCostRepository _repository;
    private readonly Func<string, bool> _hasPermission;

    public PurchaseLandedCostService(IPurchaseLandedCostRepository repository, Func<string, bool> hasPermission)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _hasPermission = hasPermission ?? throw new ArgumentNullException(nameof(hasPermission));
    }

    public Task<IReadOnlyList<PurchaseLandedCostSummary>> ListAsync(
        Guid companyId, string? search = null, CancellationToken cancellationToken = default)
    {
        Require("PUR.LANDEDCOST.VIEW");
        return _repository.ListAsync(companyId, search, cancellationToken);
    }

    public Task<IReadOnlyList<PurchaseLandedCostReceiptOption>> ListReceiptsAsync(
        Guid companyId, CancellationToken cancellationToken = default)
    {
        Require("PUR.LANDEDCOST.VIEW");
        return _repository.ListReceiptsAsync(companyId, cancellationToken);
    }

    public Task<IReadOnlyList<PurchaseLandedCostReceiptLineOption>> ListReceiptLinesAsync(
        Guid companyId, long receiptDocumentId, CancellationToken cancellationToken = default)
    {
        Require("PUR.LANDEDCOST.VIEW");
        return _repository.ListReceiptLinesAsync(companyId, receiptDocumentId, cancellationToken);
    }

    public Task<IReadOnlyList<PurchaseLandedCostPayeeOption>> ListPayeesAsync(
        Guid companyId, CancellationToken cancellationToken = default)
    {
        Require("PUR.LANDEDCOST.VIEW");
        return _repository.ListPayeesAsync(companyId, cancellationToken);
    }

    public Task<IReadOnlyList<PurchaseLandedCostLinkableDocumentOption>> ListLinkableDocumentsAsync(
        Guid companyId, CancellationToken cancellationToken = default)
    {
        Require("PUR.LANDEDCOST.VIEW");
        return _repository.ListLinkableDocumentsAsync(companyId, cancellationToken);
    }

    public Task<Guid> CreateDraftAsync(Guid companyId, IReadOnlyList<PurchaseLandedCostReceiptSelection> receipts, string documentNumber,
        DateOnly documentDate, string currencyCode, decimal exchangeRate,
        IReadOnlyList<PurchaseLandedCostChargeDraft> charges, string actor,
        CancellationToken cancellationToken = default)
    {
        Require("PUR.LANDEDCOST.EDIT");
        RequireActor(actor);
        return _repository.CreateDraftAsync(companyId, receipts, documentNumber,
            documentDate, currencyCode, exchangeRate, charges, actor, cancellationToken);
    }

    public Task<Guid> PostAsync(Guid companyId, Guid landedCostId, string actor,
        CancellationToken cancellationToken = default)
    {
        Require("PUR.LANDEDCOST.POST");
        RequireActor(actor);
        return _repository.PostAsync(companyId, landedCostId, actor, cancellationToken);
    }

    public Task<Guid> ReverseAsync(Guid companyId, Guid landedCostId, string reversalNumber,
        DateOnly reversalDate, string reason, string actor, CancellationToken cancellationToken = default)
    {
        Require("PUR.LANDEDCOST.REVERSE");
        RequireActor(actor);
        return _repository.ReverseAsync(companyId, landedCostId, reversalNumber,
            reversalDate, reason, actor, cancellationToken);
    }

    private void Require(string permission)
    {
        if (!_hasPermission(permission))
            throw new UnauthorizedAccessException($"Permission required: {permission}.");
    }

    private static void RequireActor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("An authenticated actor is required.", nameof(actor));
    }
}
