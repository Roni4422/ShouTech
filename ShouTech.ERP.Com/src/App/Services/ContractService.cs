using System.Globalization;
using ShouTech.Domain.Entities;

namespace ShouTech.Application.Services;

/// <summary>
/// Sales contract service (owner sales spec section 10, migration
/// 101). All invariants are validated here before the repository is
/// touched (the repository re-checks state inside its transactions):
/// a contract needs a party, a title, a coherent validity window and
/// at least one line; lines carry a material, a positive contracted
/// quantity and a non-negative price, never repeat a material, and a
/// cadence is (next date + positive interval + positive scheduled
/// quantity) or nothing. Status moves follow ContractTransitions;
/// renewal, expiry sweep, scheduled generation and fulfillment are
/// repository operations surfaced here. Runs under the
/// SALES.CONTRACT.* RBAC permissions only (licensing postponed).
/// </summary>
public sealed class ContractService
{
    private readonly IContractRepository _repository;

    public ContractService(IContractRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public Task<IReadOnlyList<SalesContract>> GetContractsAsync(string companyId, string? status = null, CancellationToken cancellationToken = default)
        => _repository.GetContractsAsync(RequireCompany(companyId), status, cancellationToken);

    public Task<SalesContract?> GetContractAsync(string companyId, Guid contractId, CancellationToken cancellationToken = default)
        => _repository.GetContractAsync(RequireCompany(companyId), contractId, cancellationToken);

    public Task SaveContractAsync(string companyId, SalesContract contract, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ValidateContract(contract);
        contract.Title = contract.Title.Trim();
        contract.PartyCode = string.IsNullOrWhiteSpace(contract.PartyCode) ? null : contract.PartyCode.Trim();
        contract.PartyName = string.IsNullOrWhiteSpace(contract.PartyName) ? null : contract.PartyName.Trim();
        return _repository.SaveContractAsync(RequireCompany(companyId), contract, RequireUser(userId), cancellationToken);
    }

    public Task DeleteContractAsync(string companyId, Guid contractId, string userId, CancellationToken cancellationToken = default)
    {
        if (contractId == Guid.Empty) throw new ArgumentException("Contract is required.", nameof(contractId));
        return _repository.SoftDeleteContractAsync(RequireCompany(companyId), contractId, RequireUser(userId), cancellationToken);
    }

    public Task SetStatusAsync(string companyId, Guid contractId, string toStatus, string userId, CancellationToken cancellationToken = default)
    {
        if (contractId == Guid.Empty) throw new ArgumentException("Contract is required.", nameof(contractId));
        if (!ContractStatuses.All.Contains(toStatus))
            throw new ArgumentException($"Unknown contract status '{toStatus}'.", nameof(toStatus));
        return _repository.SetStatusAsync(RequireCompany(companyId), contractId, toStatus, RequireUser(userId), cancellationToken);
    }

    public Task<SalesContract> RenewAsync(string companyId, Guid contractId, string userId, CancellationToken cancellationToken = default)
    {
        if (contractId == Guid.Empty) throw new ArgumentException("Contract is required.", nameof(contractId));
        return _repository.RenewAsync(RequireCompany(companyId), contractId, RequireUser(userId), cancellationToken);
    }

    public Task<IReadOnlyList<SalesContract>> GetExpiringAsync(string companyId, int withinDays, CancellationToken cancellationToken = default)
        => _repository.GetExpiringAsync(RequireCompany(companyId), withinDays,
            DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), cancellationToken);

    public Task<int> ExpireOverdueAsync(string companyId, string userId, CancellationToken cancellationToken = default)
        => _repository.ExpireOverdueAsync(RequireCompany(companyId),
            DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            RequireUser(userId), cancellationToken);

    public Task<ContractFulfillment?> GetFulfillmentAsync(string companyId, Guid contractId, CancellationToken cancellationToken = default)
        => _repository.GetFulfillmentAsync(RequireCompany(companyId), contractId, cancellationToken);

    public Task<int> GenerateDueDeliveriesAsync(string companyId, string userId, CancellationToken cancellationToken = default)
        => _repository.GenerateDueDeliveriesAsync(RequireCompany(companyId),
            DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            RequireUser(userId), cancellationToken);

    private static void ValidateContract(SalesContract contract)
    {
        if (contract.PartyId is null && string.IsNullOrWhiteSpace(contract.PartyCode))
            throw new InvalidOperationException("A contract requires a party.");
        if (string.IsNullOrWhiteSpace(contract.Title))
            throw new InvalidOperationException("A contract requires a title.");
        if (!DateOnly.TryParse(contract.StartDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !DateOnly.TryParse(contract.EndDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
            || end < start)
            throw new InvalidOperationException("The contract validity window is invalid (end date must not precede the start date).");
        if (contract.GuaranteeAmount < 0m)
            throw new InvalidOperationException("The guarantee amount cannot be negative.");
        if (contract.Lines.Count == 0)
            throw new InvalidOperationException("A contract requires at least one line.");
        var seen = new HashSet<long>();
        foreach (var line in contract.Lines)
        {
            if (line.MaterialId <= 0)
                throw new InvalidOperationException("Every contract line requires a material.");
            if (!seen.Add(line.MaterialId))
                throw new InvalidOperationException($"Material {line.MaterialId} appears on more than one contract line; merge the lines.");
            if (line.ContractedQty <= 0m)
                throw new InvalidOperationException($"Line for material {line.MaterialId}: contracted quantity must be positive.");
            if (line.UnitPrice < 0m)
                throw new InvalidOperationException($"Line for material {line.MaterialId}: unit price cannot be negative.");
            var hasCadence = line.NextDeliveryDate is not null || line.IntervalDays is not null || line.ScheduledQty > 0m;
            if (hasCadence && (line.NextDeliveryDate is null || line.IntervalDays is not > 0 || line.ScheduledQty <= 0m))
                throw new InvalidOperationException(
                    $"Line for material {line.MaterialId}: a delivery cadence needs a next delivery date, a positive interval in days and a positive scheduled quantity.");
            if (line.NextDeliveryDate is not null
                && !DateOnly.TryParse(line.NextDeliveryDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new InvalidOperationException($"Line for material {line.MaterialId}: the next delivery date is invalid.");
        }
    }

    private static string RequireCompany(string companyId)
        => string.IsNullOrWhiteSpace(companyId)
            ? throw new ArgumentException("Company is required.", nameof(companyId))
            : companyId.Trim();

    private static string RequireUser(string userId)
        => string.IsNullOrWhiteSpace(userId)
            ? throw new ArgumentException("User is required.", nameof(userId))
            : userId.Trim();
}
