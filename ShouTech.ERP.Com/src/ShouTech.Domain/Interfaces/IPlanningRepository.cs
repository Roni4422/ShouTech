using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

/// <summary>
/// MRP-lite planning persistence (migration 058). A run reads demand
/// (open sales-order lines + materials below reorder point), explodes it
/// through active BOMs, nets requirements against engine stock balances
/// and incoming purchase orders, and persists planned production orders
/// plus purchase suggestions. Conversions create REAL documents through
/// the existing paths (MfgOrderRepository for production,
/// OrderDocumentRepository for purchase orders) and stamp the created
/// ids back onto the planning rows in the same transaction boundary the
/// conversion service defines. Company id is the company CODE; the
/// commercial-document world is reached through the company's Guid,
/// resolved from the companies registry exactly like CRM (057) does.
/// </summary>
public interface IPlanningRepository
{
    /// <summary>Runs planning and persists the run + its rows in one
    /// transaction. Returns the persisted run with all rows.</summary>
    Task<PlanningRun> RunPlanningAsync(string companyId, string userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlanningRun>> GetRunsAsync(string companyId, CancellationToken cancellationToken = default);

    Task<PlanningRun?> GetRunAsync(string companyId, Guid runId, CancellationToken cancellationToken = default);

    /// <summary>Feasibility for one finished material + quantity, from
    /// the same explosion planning uses. Throws MFG-1002 when the
    /// material has no active BOM.</summary>
    Task<FeasibilityResult> CheckFeasibilityAsync(string companyId, string materialCode, decimal qty, CancellationToken cancellationToken = default);

    /// <summary>Converts a Planned row into a real Draft manufacturing
    /// order (BOM exploded into its lines) and stamps the order id.
    /// Throws MFG-1006 when the row is not Planned.</summary>
    Task<Guid> ConvertPlannedOrderAsync(string companyId, Guid plannedOrderId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Converts a Suggested row into a real Draft purchase
    /// order through OrderDocumentRepository and stamps doc id + number.
    /// Throws MFG-1006 when the row is not Suggested.</summary>
    Task<(long DocId, string DocNo)> ConvertSuggestionAsync(string companyId, Guid suggestionId, string userId, CancellationToken cancellationToken = default);

    Task CancelPlannedOrderAsync(string companyId, Guid plannedOrderId, string userId, CancellationToken cancellationToken = default);

    Task CancelSuggestionAsync(string companyId, Guid suggestionId, string userId, CancellationToken cancellationToken = default);
}
