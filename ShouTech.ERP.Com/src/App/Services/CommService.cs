using System.Globalization;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>
/// Sales commission service (owner sales spec Phase 09, migration 097).
/// All invariants are validated here before the repository is touched
/// (the repository re-checks state inside its transactions):
///   * a policy carries a name, a known basis, a rate inside 0-100 and
///     coherent scopes (SAL-7013); tier slabs start at &gt;= 0 with rates
///     inside 0-100 and never repeat a threshold;
///   * entries move Pending -&gt; Approved / Rejected only (SAL-7014);
///   * a settlement captures Approved entries of one rep and period,
///     moves Draft -&gt; Approved -&gt; Paid only (SAL-7014), and paying
///     posts a balanced journal voucher (Dr commission expense /
///     Cr cash) BEFORE the settlement is stamped Paid, so a failed
///     voucher leaves the settlement Approved and retryable.
///
/// FAIL-SOFT GENERATION: the Generate* methods are called from posting
/// hooks (OrderService chain wrappers, InvoiceWin after a native post).
/// A commission failure must never break invoice posting, so the hooks
/// themselves catch and report; the service methods stay honest and let
/// exceptions surface to those hooks. "No rep on the document" and "no
/// matching policy" are normal outcomes that create zero entries.
///
/// No entitlement gate: licensing is postponed by the owner; the module
/// runs under the SALES.COMMISSION.* RBAC permissions only.
/// </summary>
public sealed class CommService
{
    private readonly ICommRepository _repository;
    private readonly IJournalRepository? _journal;

    public CommService(ICommRepository repository, IJournalRepository? journal = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _journal = journal;
    }

    // ---- Policies -------------------------------------------------------
    public Task<IReadOnlyList<CommPolicy>> GetPoliciesAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetPoliciesAsync(RequireCompany(companyId), cancellationToken);

    public Task SavePolicyAsync(string companyId, CommPolicy policy, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ValidatePolicy(policy);
        policy.Name = policy.Name.Trim();
        policy.RepCode = string.IsNullOrWhiteSpace(policy.RepCode) ? null : policy.RepCode.Trim();
        policy.MaterialGroupCode = string.IsNullOrWhiteSpace(policy.MaterialGroupCode) ? null : policy.MaterialGroupCode.Trim();
        policy.AreaCode = string.IsNullOrWhiteSpace(policy.AreaCode) ? null : policy.AreaCode.Trim();
        return _repository.SavePolicyAsync(RequireCompany(companyId), policy, RequireUser(userId), cancellationToken);
    }

    public Task DeletePolicyAsync(string companyId, Guid policyId, string userId, CancellationToken cancellationToken = default)
    {
        if (policyId == Guid.Empty) throw new ArgumentException("Policy is required.", nameof(policyId));
        return _repository.SoftDeletePolicyAsync(RequireCompany(companyId), policyId, RequireUser(userId), cancellationToken);
    }

    internal static void ValidatePolicy(CommPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(policy.Name))
            throw new AppError(ErrorCodes.CommissionPolicyInvalid, "A commission policy requires a name.", policy.Name);
        if (!CommBases.All.Contains(policy.Basis))
            throw new AppError(ErrorCodes.CommissionPolicyInvalid, $"Unknown commission basis '{policy.Basis}'.", policy.Basis);
        if (policy.RatePct is < 0m or > 100m)
            throw new AppError(ErrorCodes.CommissionPolicyInvalid, $"Policy '{policy.Name}' has a rate outside 0-100%.", policy.Name);
        if (!CommRepScopes.AllScopes.Contains(policy.RepScope))
            throw new AppError(ErrorCodes.CommissionPolicyInvalid, $"Unknown rep scope '{policy.RepScope}'.", policy.RepScope);
        if (policy.RepScope == CommRepScopes.SpecificRep && string.IsNullOrWhiteSpace(policy.RepCode))
            throw new AppError(ErrorCodes.CommissionPolicyInvalid, $"Policy '{policy.Name}' is scoped to a specific rep but names none.", policy.Name);
        if (!CommProductScopes.AllScopes.Contains(policy.ProductScope))
            throw new AppError(ErrorCodes.CommissionPolicyInvalid, $"Unknown product scope '{policy.ProductScope}'.", policy.ProductScope);
        if (policy.ProductScope == CommProductScopes.Material && policy.MaterialId is null)
            throw new AppError(ErrorCodes.CommissionPolicyInvalid, $"Policy '{policy.Name}' is scoped to a material but names none.", policy.Name);
        if (policy.ProductScope == CommProductScopes.Group && string.IsNullOrWhiteSpace(policy.MaterialGroupCode))
            throw new AppError(ErrorCodes.CommissionPolicyInvalid, $"Policy '{policy.Name}' is scoped to a material group but names none.", policy.Name);
        if (!CommCustomerScopes.AllScopes.Contains(policy.CustomerScope))
            throw new AppError(ErrorCodes.CommissionPolicyInvalid, $"Unknown customer scope '{policy.CustomerScope}'.", policy.CustomerScope);
        if (policy.CustomerScope == CommCustomerScopes.SpecificParty && policy.PartyId is null)
            throw new AppError(ErrorCodes.CommissionPolicyInvalid, $"Policy '{policy.Name}' is scoped to a customer but names none.", policy.Name);
        if (!string.IsNullOrWhiteSpace(policy.ValidFrom) && !string.IsNullOrWhiteSpace(policy.ValidTo) &&
            string.CompareOrdinal(policy.ValidFrom, policy.ValidTo) > 0)
            throw new AppError(ErrorCodes.CommissionPolicyInvalid, $"Policy '{policy.Name}' ends before it starts.", policy.Name);
        var thresholds = new HashSet<decimal>();
        foreach (var tier in policy.Tiers)
        {
            if (tier.FromAmount < 0m)
                throw new AppError(ErrorCodes.CommissionPolicyInvalid, $"Policy '{policy.Name}' has a tier starting below zero.", policy.Name);
            if (tier.RatePct is < 0m or > 100m)
                throw new AppError(ErrorCodes.CommissionPolicyInvalid, $"Policy '{policy.Name}' has a tier rate outside 0-100%.", policy.Name);
            if (!thresholds.Add(tier.FromAmount))
                throw new AppError(ErrorCodes.CommissionPolicyInvalid, $"Policy '{policy.Name}' repeats the tier threshold {tier.FromAmount}.", policy.Name);
        }
    }

    // ---- Entries ----------------------------------------------------------
    public Task<IReadOnlyList<CommEntry>> GetEntriesAsync(string companyId, string? repCode = null, string? status = null,
        string? periodFrom = null, string? periodTo = null, CancellationToken cancellationToken = default)
        => _repository.GetEntriesAsync(RequireCompany(companyId), repCode, status, periodFrom, periodTo, cancellationToken);

    public async Task ApproveEntriesAsync(string companyId, IReadOnlyList<Guid> entryIds, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        var company = RequireCompany(companyId);
        var user = RequireUser(userId);
        foreach (var entryId in entryIds)
        {
            var rows = await _repository.SetEntryStatusAsync(company, entryId, CommEntryStatuses.Pending, CommEntryStatuses.Approved, user, cancellationToken).ConfigureAwait(false);
            if (rows == 0)
                throw new AppError(ErrorCodes.CommissionSettlementInvalid,
                    "Only a Pending commission entry can be approved.", entryId.ToString("D"));
        }
    }

    public async Task RejectEntriesAsync(string companyId, IReadOnlyList<Guid> entryIds, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        var company = RequireCompany(companyId);
        var user = RequireUser(userId);
        foreach (var entryId in entryIds)
        {
            var rows = await _repository.SetEntryStatusAsync(company, entryId, CommEntryStatuses.Pending, CommEntryStatuses.Rejected, user, cancellationToken).ConfigureAwait(false);
            if (rows == 0)
                throw new AppError(ErrorCodes.CommissionSettlementInvalid,
                    "Only a Pending commission entry can be rejected.", entryId.ToString("D"));
        }
    }

    // ---- Generation (fail-soft hooks call these) ---------------------------
    /// <summary>Sales/Profit entries for a posted invoice. The company
    /// key may be the document's Guid key or the company code — the
    /// repository resolves it. Safe to call twice (idempotent).</summary>
    public Task<int> GenerateForInvoiceAsync(string companyKey, long docId, string userId, CancellationToken cancellationToken = default)
    {
        if (docId <= 0) throw new ArgumentOutOfRangeException(nameof(docId));
        return _repository.GenerateForInvoiceAsync(RequireCompany(companyKey), docId, RequireUser(userId), cancellationToken);
    }

    /// <summary>Negative return adjustments for a posted SRT document.</summary>
    public Task<int> GenerateForReturnAsync(string companyKey, long returnDocId, string userId, CancellationToken cancellationToken = default)
    {
        if (returnDocId <= 0) throw new ArgumentOutOfRangeException(nameof(returnDocId));
        return _repository.GenerateForReturnAsync(RequireCompany(companyKey), returnDocId, RequireUser(userId), cancellationToken);
    }

    /// <summary>Collection-basis period run over payment_allocations.</summary>
    public Task<int> GenerateCollectionForPeriodAsync(string companyId, string periodFrom, string periodTo, string userId, CancellationToken cancellationToken = default)
    {
        var company = RequireCompany(companyId);
        RequirePeriod(periodFrom, periodTo);
        return _repository.GenerateCollectionForPeriodAsync(company, periodFrom.Trim(), periodTo.Trim(), RequireUser(userId), cancellationToken);
    }

    // ---- Settlements ---------------------------------------------------------
    public Task<IReadOnlyList<CommSettlement>> GetSettlementsAsync(string companyId, string? repCode = null, CancellationToken cancellationToken = default)
        => _repository.GetSettlementsAsync(RequireCompany(companyId), repCode, cancellationToken);

    public Task<CommSettlement> CreateSettlementAsync(string companyId, string repCode, string periodFrom, string periodTo, string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repCode)) throw new ArgumentException("A sales rep is required.", nameof(repCode));
        RequirePeriod(periodFrom, periodTo);
        return _repository.CreateSettlementAsync(RequireCompany(companyId), repCode.Trim(), periodFrom.Trim(), periodTo.Trim(), RequireUser(userId), cancellationToken);
    }

    public async Task ApproveSettlementAsync(string companyId, Guid settlementId, string userId, CancellationToken cancellationToken = default)
    {
        if (settlementId == Guid.Empty) throw new ArgumentException("Settlement is required.", nameof(settlementId));
        var rows = await _repository.SetSettlementStatusAsync(RequireCompany(companyId), settlementId,
            CommSettlementStatuses.Draft, CommSettlementStatuses.Approved, null, RequireUser(userId), cancellationToken).ConfigureAwait(false);
        if (rows == 0)
            throw new AppError(ErrorCodes.CommissionSettlementInvalid,
                "Only a Draft commission settlement can be approved.", settlementId.ToString("D"));
    }

    /// <summary>Pays an Approved settlement: resolves the commission
    /// expense account (first postable chart account named for
    /// commissions) and the Cash account, posts a balanced journal
    /// voucher Dr expense / Cr cash with the rep on the description,
    /// then stamps the settlement Paid with the journal document number
    /// and flips its entries to Settled. Reps are not parties, so no
    /// party ledger row is written — the rep is carried on the
    /// settlement and the voucher text. A settlement whose total is not
    /// positive (return adjustments can outweigh earnings) cannot be
    /// paid out; it stays Approved for manual handling (SAL-7014).</summary>
    public async Task<string> PaySettlementAsync(string companyId, Guid settlementId, string userId, CancellationToken cancellationToken = default)
    {
        if (settlementId == Guid.Empty) throw new ArgumentException("Settlement is required.", nameof(settlementId));
        var company = RequireCompany(companyId);
        var user = RequireUser(userId);
        var journal = _journal ?? throw new AppError(ErrorCodes.CommissionSettlementInvalid,
            "The journal engine is not available; commission settlements cannot be paid.", settlementId.ToString("D"));

        var settlement = await _repository.GetSettlementAsync(company, settlementId, cancellationToken).ConfigureAwait(false)
            ?? throw new AppError(ErrorCodes.CommissionSettlementInvalid, "The commission settlement was not found.", settlementId.ToString("D"));
        if (settlement.Status != CommSettlementStatuses.Approved)
            throw new AppError(ErrorCodes.CommissionSettlementInvalid,
                $"Settlement for rep '{settlement.RepCode}' is {settlement.Status}; only an Approved settlement can be paid.", settlement.RepCode);
        if (settlement.TotalAmount <= 0m)
            throw new AppError(ErrorCodes.CommissionSettlementInvalid,
                $"Settlement for rep '{settlement.RepCode}' totals {settlement.TotalAmount}; a non-positive settlement cannot be paid out.", settlement.RepCode);

        var accounts = await _repository.ResolvePaymentAccountsAsync(company, cancellationToken).ConfigureAwait(false)
            ?? throw new AppError(ErrorCodes.CommissionSettlementInvalid,
                "No postable commission expense account (an account named for commissions) or no postable Cash account exists in this company's chart.", company);

        // The voucher is dated at the settlement period's end: paying a
        // period's commissions is an event OF that period, and the date
        // stays deterministic for audit (and for the period's fiscal
        // calendar to accept or refuse the posting honestly).
        var voucherDate = DateOnly.Parse(settlement.PeriodTo, CultureInfo.InvariantCulture);
        var documentNumber = $"SCO-{settlementId.ToString("N")[..8].ToUpperInvariant()}";
        var entry = new JournalEntry(voucherDate,
            $"Sales commission settlement {settlement.PeriodFrom}..{settlement.PeriodTo} — rep {settlement.RepCode}",
            referenceNumber: documentNumber,
            currencyCode: accounts.CurrencyCode,
            exchangeRate: 1m,
            documentNumber: documentNumber);
        entry.AddLine(accounts.ExpenseAccountId, settlement.TotalAmount, 0m, $"Commission expense — rep {settlement.RepCode}");
        entry.AddLine(accounts.CreditAccountId, 0m, settlement.TotalAmount, $"Commission paid — rep {settlement.RepCode}");
        entry.EnsureBalanced();
        await journal.SaveDraftAsync(accounts.CompanyGuid, entry, user, cancellationToken).ConfigureAwait(false);
        await journal.PostAsync(entry.Id, user, cancellationToken).ConfigureAwait(false);

        var rows = await _repository.SetSettlementStatusAsync(company, settlementId,
            CommSettlementStatuses.Approved, CommSettlementStatuses.Paid, documentNumber, user, cancellationToken).ConfigureAwait(false);
        if (rows == 0)
            throw new AppError(ErrorCodes.CommissionSettlementInvalid,
                $"Settlement for rep '{settlement.RepCode}' changed state while its voucher posted; voucher {documentNumber} needs reconciliation.", settlement.RepCode);
        return documentNumber;
    }

    // ---- Lookups ---------------------------------------------------------------
    public Task<IReadOnlyList<(string Code, string Name)>> GetRepOptionsAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetRepOptionsAsync(RequireCompany(companyId), cancellationToken);

    private static void RequirePeriod(string periodFrom, string periodTo)
    {
        if (string.IsNullOrWhiteSpace(periodFrom) || string.IsNullOrWhiteSpace(periodTo))
            throw new ArgumentException("A period (from and to dates) is required.");
        if (string.CompareOrdinal(periodFrom.Trim(), periodTo.Trim()) > 0)
            throw new AppError(ErrorCodes.CommissionSettlementInvalid,
                $"The period {periodFrom}..{periodTo} ends before it starts.", periodFrom);
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
