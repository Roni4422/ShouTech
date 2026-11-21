using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>
/// POS application service (migration 066, docs/BUILD_POS.md). All
/// invariants are validated here before the repository is touched (the
/// repository re-checks state inside its transactions, and the posting
/// path re-checks the session inside ITS transaction):
///   * a session opens with a non-negative counted float, on one
///     terminal, for one cashier (POS-1002 on a second open);
///   * sale tenders must settle the bill exactly — over-tendered cash
///     is change, on-account needs a named party (POS-1004/1005, the
///     pure PosTenderMath);
///   * a sale outside an open session is refused with POS-1001 unless
///     the company EXPLICITLY enabled session-less mode in pos_settings
///     AND the caller holds the bypass permission (POS-1010);
///   * return quantities never exceed what the original bill still
///     has returnable (POS-1006).
/// </summary>
public sealed class PosService
{
    private readonly IPosRepository _repository;

    public PosService(IPosRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    internal IPosRepository Repository => _repository;

    // ---- Sessions -------------------------------------------------------
    public async Task<PosSession> OpenSessionAsync(string companyId, string terminalCode, string cashier, int? warehouseId, string currencyCode, decimal openingCash, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (string.IsNullOrWhiteSpace(terminalCode))
            throw new AppError(ErrorCodes.PosSessionStateInvalid, "A terminal code is required to open a POS session.");
        if (!warehouseId.HasValue || warehouseId.Value <= 0)
            throw new AppError(ErrorCodes.PosSessionStateInvalid, "An active warehouse is required to open a POS session.");
        if (openingCash < 0)
            throw new AppError(ErrorCodes.PosSessionStateInvalid, "The opening cash count cannot be negative.");
        return await _repository.OpenSessionAsync(companyId, terminalCode.Trim(), cashier ?? string.Empty, warehouseId, currencyCode, openingCash, RequireUser(userId), cancellationToken).ConfigureAwait(false);
    }

    public Task<PosSession?> GetOpenSessionAsync(string companyId, string terminalCode, CancellationToken cancellationToken = default)
        => _repository.GetOpenSessionAsync(RequireCompany(companyId), terminalCode, cancellationToken);

    public Task<int?> GetTerminalWarehouseIdAsync(string companyId, string terminalCode, CancellationToken cancellationToken = default)
        => _repository.GetTerminalWarehouseIdAsync(RequireCompany(companyId), terminalCode, cancellationToken);

    public Task<PosSession?> GetSessionAsync(string companyId, Guid sessionId, CancellationToken cancellationToken = default)
        => _repository.GetSessionAsync(RequireCompany(companyId), sessionId, cancellationToken);

    public Task<IReadOnlyList<PosSession>> GetSessionsAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetSessionsAsync(RequireCompany(companyId), cancellationToken);

    public Task<PosZReport> CloseSessionAsync(string companyId, Guid sessionId, decimal countedCash, string userId, CancellationToken cancellationToken = default)
    {
        if (countedCash < 0)
            throw new AppError(ErrorCodes.PosSessionStateInvalid, "The counted cash cannot be negative.");
        return _repository.CloseSessionAsync(RequireCompany(companyId), sessionId, countedCash, RequireUser(userId), cancellationToken);
    }

    /// <summary>The posting gate every POS sale/return passes through:
    /// returns the open session for the terminal, or — only when the
    /// company enabled session-less mode AND the caller may bypass —
    /// null (an explicit session-less sale). Anything else is POS-1001
    /// (no session, mode off) or POS-1010 (mode on, no bypass right).</summary>
    public async Task<PosSession?> RequireSessionForSaleAsync(string companyId, string terminalCode, bool callerMayBypassSession, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var open = await _repository.GetOpenSessionAsync(companyId, terminalCode, cancellationToken).ConfigureAwait(false);
        if (open is not null) return open;
        var settings = await _repository.GetSettingsAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (!settings.AllowSessionless)
            throw new AppError(ErrorCodes.PosNoOpenSession,
                $"No POS session is open on terminal '{terminalCode}' and this company has not enabled session-less selling. Open a session before selling.", terminalCode);
        if (!callerMayBypassSession)
            throw new AppError(ErrorCodes.PosSessionlessNotAllowed,
                "Session-less selling is enabled for this company, but this user does not hold the POS.SESSION.BYPASS permission.", terminalCode);
        return null;
    }

    // ---- Settings ---------------------------------------------------------
    public Task<PosSettings> GetSettingsAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetSettingsAsync(RequireCompany(companyId), cancellationToken);

    public Task SaveSettingsAsync(string companyId, PosSettings settings, string userId, CancellationToken cancellationToken = default)
        => _repository.SaveSettingsAsync(RequireCompany(companyId), settings, RequireUser(userId), cancellationToken);

    // ---- Products -----------------------------------------------------------
    public Task<IReadOnlyList<PosProduct>> SearchProductsAsync(string companyId, string? term, int? warehouseId = null, int limit = 60, CancellationToken cancellationToken = default)
        => _repository.SearchProductsAsync(RequireCompany(companyId), term, warehouseId, limit, cancellationToken);

    // ---- Tenders ----------------------------------------------------------------
    /// <summary>Validates a sale's tenders (pure math) and returns the
    /// change due. The windows call this before posting; the posting
    /// path re-validates inside its transaction.</summary>
    public decimal ValidateSaleTenders(decimal billTotal, IReadOnlyList<PosTender> tenders, bool hasParty)
        => PosTenderMath.ValidateSaleTenders(billTotal, tenders, hasParty);

    public Task<IReadOnlyList<PosTender>> GetTendersForDocAsync(string companyId, long docId, string docKind, CancellationToken cancellationToken = default)
        => _repository.GetTendersForDocAsync(RequireCompany(companyId), docId, docKind, cancellationToken);

    // ---- Held bills ---------------------------------------------------------------
    public Task<PosHeldBill> HoldBillAsync(string companyId, PosHeldBill bill, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(bill);
        if (bill.Lines.Count == 0)
            throw new AppError(ErrorCodes.PosHeldBillStateInvalid, "A held bill needs at least one line.");
        if (bill.Lines.Any(l => l.Qty <= 0 || l.UnitPrice < 0))
            throw new AppError(ErrorCodes.PosHeldBillStateInvalid, "Every held line needs a positive quantity and a non-negative price.");
        if (bill.BillDiscount < 0)
            throw new AppError(ErrorCodes.PosHeldBillStateInvalid, "A bill discount cannot be negative.");
        return _repository.HoldBillAsync(companyId, bill, RequireUser(userId), cancellationToken);
    }

    public Task<IReadOnlyList<PosHeldBill>> GetHeldBillsAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetHeldBillsAsync(RequireCompany(companyId), cancellationToken);

    public Task<PosHeldBill> RecallHeldBillAsync(string companyId, Guid heldId, string userId, CancellationToken cancellationToken = default)
        => _repository.RecallHeldBillAsync(RequireCompany(companyId), heldId, RequireUser(userId), cancellationToken);

    public Task CancelHeldBillAsync(string companyId, Guid heldId, string userId, CancellationToken cancellationToken = default)
        => _repository.CancelHeldBillAsync(RequireCompany(companyId), heldId, RequireUser(userId), cancellationToken);

    // ---- Returns / Z ---------------------------------------------------------------
    public async Task<PosOriginalBill> GetOriginalBillAsync(string companyId, string docNo, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        return await _repository.GetOriginalBillAsync(companyId, docNo, cancellationToken).ConfigureAwait(false)
            ?? throw new AppError(ErrorCodes.PosReturnOriginalInvalid,
                $"No posted POS bill '{docNo}' exists for this company.", docNo ?? string.Empty);
    }

    /// <summary>Validates a return selection against the original bill
    /// (POS-1006) — the posting path re-validates against fresh data.</summary>
    public void ValidateReturnQuantities(PosOriginalBill original, IReadOnlyDictionary<int, decimal> qtyByLineNo)
        => PosTenderMath.ValidateReturnQuantities(original, qtyByLineNo);

    public Task<PosZReport> GetZReportAsync(string companyId, Guid sessionId, CancellationToken cancellationToken = default)
        => _repository.GetZReportAsync(RequireCompany(companyId), sessionId, cancellationToken);

    private static string RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
        return companyId;
    }

    private static string RequireUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("User is required.", nameof(userId));
        return userId;
    }
}
