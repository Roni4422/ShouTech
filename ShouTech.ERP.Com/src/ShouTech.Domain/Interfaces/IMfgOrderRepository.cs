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

    // ---- Staged workflow (migration 058) --------------------------------
    // Draft -> Confirmed -> InProcess -> Completed -> Closed, with WIP +
    // variance GL through GlPosting (source types MFG-ISSUE / MFG-FINISH
    // / MFG-OVHD / MFG-CLOSE). Every stage action is ONE transaction:
    // stock movements (valuation engine), GL entry, header/line actuals
    // and the mfg_production_events audit row commit or roll back
    // together. Cancel of a staged order reverses exactly what its
    // stages posted, stock and GL, symmetrically.

    /// <summary>Draft → Confirmed. No stock or GL effects; records the
    /// Confirm event. The order's lines are frozen from here on (edits
    /// require a new order).</summary>
    Task<MfgStageResult> ConfirmAsync(string companyId, Guid orderId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Issues the given RAW quantities (partial issues allowed,
    /// several issues per order). Each issued line is costed by the
    /// valuation engine from its source warehouse; GL Dr WIP / Cr
    /// Inventory per component. First issue moves Confirmed → InProcess.</summary>
    Task<MfgStageResult> IssueAsync(string companyId, Guid orderId, IReadOnlyList<MfgIssueLine> issues, string userId, CancellationToken cancellationToken = default);

    /// <summary>Receives finished quantity (partial receipts allowed)
    /// plus the given by-product / scrap line quantities. The receipt is
    /// valued from the order's WIP pool (accumulated cost attributable
    /// to this receipt, overhead absorbed pro-rata, by-products taking
    /// their cost share out of the pool): GL Dr Finished Goods (+ Dr
    /// by-product inventory) / Cr WIP. Reaching the planned quantity
    /// moves the order to Completed.</summary>
    Task<MfgStageResult> FinishAsync(string companyId, Guid orderId, decimal finishedQty, IReadOnlyList<MfgReceiptLine> receipts, string userId, CancellationToken cancellationToken = default);

    /// <summary>Closes an InProcess / Completed order: the remaining WIP
    /// balance is settled to the manufacturing variance account
    /// (Dr/Cr Variance vs WIP) so WIP ends at exactly zero, the standard
    /// variance is recorded, and the order becomes Closed.</summary>
    Task<MfgStageResult> CloseAsync(string companyId, Guid orderId, string userId, CancellationToken cancellationToken = default);

    /// <summary>The order's staged action history (mfg_production_events).</summary>
    Task<IReadOnlyList<MfgProductionEvent>> GetEventsAsync(string companyId, Guid orderId, CancellationToken cancellationToken = default);
}
