using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

/// <summary>
/// Persistence contract for the Definition module (ribbon completion F1).
/// Generic definition-set CRUD is driven entirely by <see cref="DefSet"/>
/// descriptors from the static registry; SQL identifiers come only from those
/// descriptors (allow-list), values are always parameterized, and every
/// statement is company-scoped. Dedicated operations cover installments
/// (schedule generation + collection posting to party_ledger), loyalty
/// points, and the delivery desk — each in a single transaction.
/// Implemented provider-neutrally (SQLite + PostgreSQL) in Infrastructure.
/// </summary>
public interface IDefinitionRepository
{
    // ------------------------------------------------------------ generic sets
    Task<IReadOnlyList<DefRow>> ListAsync(DefSet set, string companyId, CancellationToken cancellationToken = default);

    /// <summary>Insert (id null/blank) or update a row; returns the row id. Uniqueness is re-checked inside the transaction.</summary>
    Task<string> SaveAsync(DefSet set, string companyId, string? id, IReadOnlyDictionary<string, object?> values, string actor, CancellationToken cancellationToken = default);

    Task SoftDeleteAsync(DefSet set, string companyId, string id, string actor, CancellationToken cancellationToken = default);
    Task SetActiveAsync(DefSet set, string companyId, string id, bool isActive, string actor, CancellationToken cancellationToken = default);

    // ------------------------------------------------------------ installments
    Task<string> CreateInstallmentAsync(string companyId, InstallmentPlan plan, IReadOnlyList<InstallmentScheduleRow> schedule, string actor, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InstallmentPlan>> ListInstallmentsAsync(string companyId, string? partyCode, string? status, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InstallmentScheduleRow>> ListScheduleAsync(string companyId, string installmentId, CancellationToken cancellationToken = default);
    Task SoftDeleteInstallmentAsync(string companyId, string installmentId, string actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a collection against one schedule row AND inserts the matching
    /// party_ledger credit (party_kind 'C', doc_kind 'INS') in the same
    /// transaction. Returns the updated schedule row.
    /// </summary>
    Task<InstallmentScheduleRow> CollectInstallmentAsync(string companyId, string scheduleId, decimal amount, DateOnly paidDate, string actor, CancellationToken cancellationToken = default);

    // ----------------------------------------------------------------- loyalty
    Task<IReadOnlyList<LoyaltyAccount>> ListLoyaltyAccountsAsync(string companyId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LoyaltyEntry>> ListLoyaltyEntriesAsync(string companyId, string accountId, CancellationToken cancellationToken = default);

    /// <summary>Applies a signed points delta: writes the entry and updates the balance atomically; negative results are rejected.</summary>
    Task<decimal> AdjustLoyaltyAsync(string companyId, string partyCode, decimal pointsDelta, string? docRef, string? notes, string actor, CancellationToken cancellationToken = default);

    // ---------------------------------------------------------------- delivery
    Task<IReadOnlyList<DeliveryRequest>> ListDeliveryRequestsAsync(string companyId, CancellationToken cancellationToken = default);
    Task<string> SaveDeliveryRequestAsync(string companyId, DeliveryRequest request, string actor, CancellationToken cancellationToken = default);
    Task SetDeliveryStatusAsync(string companyId, string requestId, string newStatus, string? driverCode, string actor, CancellationToken cancellationToken = default);
    Task SoftDeleteDeliveryRequestAsync(string companyId, string requestId, string actor, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DriverSettlement>> ListDriverSettlementsAsync(string companyId, CancellationToken cancellationToken = default);

    /// <summary>Creates a Closed driver cash settlement (net = collected - fees) in one transaction.</summary>
    Task<string> CloseDriverCashAsync(string companyId, string driverCode, DateOnly settleDate, decimal collectedAmount, decimal feesAmount, string actor, CancellationToken cancellationToken = default);
}
