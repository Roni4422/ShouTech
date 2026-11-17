using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

/// <summary>
/// Persistence contract for the MfgOrder module (ribbon completion pass).
/// Implemented provider-neutrally (SQLite + PostgreSQL) in Infrastructure.
/// Company id is the company code stored in mfg_orders.company_id (the same
/// convention as material_stock_movements, which posting writes to).
/// </summary>
public interface IMfgOrderRepository
{
    Task<IReadOnlyList<MfgOrder>> GetAllAsync(string companyId, CancellationToken cancellationToken = default);

    Task<MfgOrder?> GetByDocNoAsync(string companyId, int docNo, CancellationToken cancellationToken = default);

    Task<int> GetNextDocNoAsync(string companyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a draft (insert or replace of header + lines) in one transaction.
    /// When DocNo is 0 the next per-company number is assigned inside the
    /// transaction. Posted/cancelled orders are never overwritten.
    /// </summary>
    Task SaveDraftAsync(string companyId, MfgOrder order, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts in one transaction: validates on-hand stock for every RAW line
    /// (SUM(qty_in - qty_out) over material_stock_movements), writes the
    /// consumption and production movements plus balance updates, computes
    /// total/unit cost, and marks the order Posted.
    /// </summary>
    Task PostAsync(string companyId, Guid orderId, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels in one transaction. A draft is simply marked Cancelled; a
    /// posted order is reversed with opposite stock movements first.
    /// </summary>
    Task CancelAsync(string companyId, Guid orderId, string userId, CancellationToken cancellationToken = default);
}
