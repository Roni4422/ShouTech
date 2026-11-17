using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>
/// Manufacturing orders service (RS-8): validation lives here; the
/// repository owns the transactional stock posting (consume RAW lines,
/// produce the finished item at the computed unit cost, snapshot the
/// standard unit cost for the variance reports).
/// </summary>
public sealed class MfgOrderService
{
    private readonly IMfgOrderRepository _repository;
    public MfgOrderService(IMfgOrderRepository repository)
        => _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    internal IMfgOrderRepository Repository => _repository;

    public Task<IReadOnlyList<MfgOrder>> GetAllAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetAllAsync(RequireCompany(companyId), cancellationToken);

    public Task<MfgOrder?> GetByDocNoAsync(string companyId, int docNo, CancellationToken cancellationToken = default)
    {
        if (docNo <= 0) throw new ArgumentException("Document number must be positive.", nameof(docNo));
        return _repository.GetByDocNoAsync(RequireCompany(companyId), docNo, cancellationToken);
    }

    public Task<int> GetNextDocNoAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetNextDocNoAsync(RequireCompany(companyId), cancellationToken);

    public async Task<Guid> SaveDraftAsync(string companyId, MfgOrder order, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        Validate(order);
        await _repository.SaveDraftAsync(companyId, order, RequireUser(userId), cancellationToken).ConfigureAwait(false);
        return order.Id;
    }

    public Task PostAsync(string companyId, Guid orderId, string userId, CancellationToken cancellationToken = default)
    {
        if (orderId == Guid.Empty) throw new ArgumentException("Order is required.", nameof(orderId));
        return _repository.PostAsync(RequireCompany(companyId), orderId, RequireUser(userId), cancellationToken);
    }

    public Task CancelAsync(string companyId, Guid orderId, string userId, CancellationToken cancellationToken = default)
    {
        if (orderId == Guid.Empty) throw new ArgumentException("Order is required.", nameof(orderId));
        return _repository.CancelAsync(RequireCompany(companyId), orderId, RequireUser(userId), cancellationToken);
    }

    private static void Validate(MfgOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (string.IsNullOrWhiteSpace(order.ItemCode))
            throw new InvalidOperationException("The finished material code is required.");
        if (order.Qty <= 0)
            throw new InvalidOperationException("The produced quantity must be greater than zero.");
        if (order.OverheadAmount < 0)
            throw new InvalidOperationException("Overhead amount cannot be negative.");
        if (order.StandardUnitCost < 0)
            throw new InvalidOperationException("Standard unit cost cannot be negative.");
        if (order.Lines is null || order.Lines.Count == 0)
            throw new InvalidOperationException("A manufacturing order requires at least one raw-material line.");
        foreach (var line in order.Lines)
        {
            if (!MfgLineKinds.All.Contains(line.LineKind))
                throw new InvalidOperationException($"Unknown manufacturing line kind '{line.LineKind}'.");
            if (string.IsNullOrWhiteSpace(line.ItemCode))
                throw new InvalidOperationException("Every manufacturing line requires a material code.");
            if (line.Qty <= 0)
                throw new InvalidOperationException($"Line quantity for '{line.ItemCode}' must be greater than zero.");
            if (line.UnitCost < 0 || line.StandardUnitCost < 0)
                throw new InvalidOperationException($"Line costs for '{line.ItemCode}' cannot be negative.");
            line.ItemCode = line.ItemCode.Trim();
        }
        order.ItemCode = order.ItemCode.Trim();
        if (string.IsNullOrWhiteSpace(order.UnitCode)) order.UnitCode = "PCS";
    }

    private static string RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
        return companyId;
    }

    private static string RequireUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException("A current user is required for audited writes.");
        return userId;
    }
}
