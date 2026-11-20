using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

/// <summary>POS session / tender / held-bill persistence (migration
/// 066, docs/BUILD_POS.md). POS lives in the party world, so the
/// company is keyed by its CODE. Document POSTING itself (GL, stock,
/// party settlement) stays in the verified InvRepo path — this
/// repository owns the till around it: sessions, tenders, held bills,
/// product search, return validation data and the Z report.</summary>
public interface IPosRepository
{
    // ---- Sessions -------------------------------------------------------
    /// <summary>Opens a session with its opening cash count. Refuses
    /// with POS-1002 when the terminal already has an open session.</summary>
    Task<PosSession> OpenSessionAsync(string companyId, string terminalCode, string cashier, int? warehouseId, string currencyCode, decimal openingCash, string userId, CancellationToken cancellationToken = default);
    Task<PosSession?> GetOpenSessionAsync(string companyId, string terminalCode, CancellationToken cancellationToken = default);
    Task<PosSession?> GetSessionAsync(string companyId, Guid sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PosSession>> GetSessionsAsync(string companyId, CancellationToken cancellationToken = default);
    /// <summary>Closes a session: expected cash and the variance are
    /// computed from the session's real tenders inside the closing
    /// transaction, stored on the session, mirrored to the legacy
    /// pos_cash_closings register, and returned as the Z report.</summary>
    Task<PosZReport> CloseSessionAsync(string companyId, Guid sessionId, decimal countedCash, string userId, CancellationToken cancellationToken = default);

    // ---- Settings ---------------------------------------------------------
    Task<PosSettings> GetSettingsAsync(string companyId, CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(string companyId, PosSettings settings, string userId, CancellationToken cancellationToken = default);

    // ---- Products -----------------------------------------------------------
    /// <summary>Contains-search over the company's materials (code,
    /// barcodes, names) for the touch grid and the search box; exact
    /// code/barcode matches sort first (the TaxSvc.ResolveMaterial
    /// ordering, generalised to a result list).</summary>
    Task<IReadOnlyList<PosProduct>> SearchProductsAsync(string companyId, string? term, int? warehouseId = null, int limit = 60, CancellationToken cancellationToken = default);

    // ---- Tenders --------------------------------------------------------------
    Task RecordTendersAsync(string companyId, IReadOnlyList<PosTender> tenders, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PosTender>> GetTendersForDocAsync(string companyId, long docId, string docKind, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PosTender>> GetTendersForSessionAsync(string companyId, Guid sessionId, CancellationToken cancellationToken = default);

    // ---- Held bills --------------------------------------------------------------
    Task<PosHeldBill> HoldBillAsync(string companyId, PosHeldBill bill, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PosHeldBill>> GetHeldBillsAsync(string companyId, CancellationToken cancellationToken = default);
    Task<PosHeldBill?> GetHeldBillAsync(string companyId, Guid heldId, CancellationToken cancellationToken = default);
    /// <summary>Marks a held bill Recalled and returns it with lines —
    /// a bill can be recalled exactly once (POS-1009 afterwards).</summary>
    Task<PosHeldBill> RecallHeldBillAsync(string companyId, Guid heldId, string userId, CancellationToken cancellationToken = default);
    Task CancelHeldBillAsync(string companyId, Guid heldId, string userId, CancellationToken cancellationToken = default);

    // ---- Returns / Z ----------------------------------------------------------------
    /// <summary>The posted POS bill a return is taken against, with
    /// per-line already-returned quantities from earlier POS returns.</summary>
    Task<PosOriginalBill?> GetOriginalBillAsync(string companyId, string docNo, CancellationToken cancellationToken = default);
    Task<PosZReport> GetZReportAsync(string companyId, Guid sessionId, CancellationToken cancellationToken = default);
}
