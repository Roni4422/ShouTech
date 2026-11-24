namespace ShouTech.Domain.Entities;

/// <summary>Commission calculation bases (owner sales spec Phase 09,
/// migration 097). Sales = stored invoice grand total; Collection =
/// allocated (actually collected) amount; Profit = revenue net of tax
/// minus the COGS the posting's stock movements carried.</summary>
public static class CommBases
{
    public const string Sales = "Sales";
    public const string Collection = "Collection";
    public const string Profit = "Profit";

    public static readonly IReadOnlyList<string> All = new[] { Sales, Collection, Profit };
}

/// <summary>Rep scope of a commission policy.</summary>
public static class CommRepScopes
{
    public const string All = "All";
    public const string SpecificRep = "SpecificRep";

    public static readonly IReadOnlyList<string> AllScopes = new[] { All, SpecificRep };
}

/// <summary>Product scope of a commission policy: every material, one
/// material, or one material group (materials.group_code).</summary>
public static class CommProductScopes
{
    public const string All = "All";
    public const string Material = "Material";
    public const string Group = "Group";

    public static readonly IReadOnlyList<string> AllScopes = new[] { All, Material, Group };
}

/// <summary>Customer scope of a commission policy. SpecificParty keys on
/// the sales_documents party_id (the party ledger id documents carry).</summary>
public static class CommCustomerScopes
{
    public const string All = "All";
    public const string SpecificParty = "SpecificParty";

    public static readonly IReadOnlyList<string> AllScopes = new[] { All, SpecificParty };
}

/// <summary>Commission entry lifecycle: generated Pending, then Approved
/// or Rejected, then Settled when its settlement is paid.</summary>
public static class CommEntryStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Settled = "Settled";

    public static readonly IReadOnlyList<string> All = new[] { Pending, Approved, Rejected, Settled };
}

/// <summary>Settlement lifecycle: Draft (entries summed, not yet
/// payable), Approved, Paid (journal voucher posted, entries Settled).</summary>
public static class CommSettlementStatuses
{
    public const string Draft = "Draft";
    public const string Approved = "Approved";
    public const string Paid = "Paid";

    public static readonly IReadOnlyList<string> All = new[] { Draft, Approved, Paid };
}

/// <summary>Source document kinds stored on commission entries. SAL is a
/// posted sales invoice (sales_documents doc_kind), SRT a posted sales
/// return (negative adjustment), ALLOC a payment allocation
/// (payment_allocations row, identified by source_ref).</summary>
public static class CommSourceKinds
{
    public const string SalesInvoice = "SAL";
    public const string SalesReturn = "SRT";
    public const string Allocation = "ALLOC";
}

/// <summary>One commission rate definition (migration 097,
/// sco_commission_policies). Policies vary by company, rep, product,
/// customer, territory and period (spec section 9). Rate resolution
/// picks the MOST SPECIFIC matching active policy:
/// rep+material &gt; rep+group &gt; rep+party &gt; rep-only &gt; company
/// default; a tier slab overrides the flat rate when one applies.</summary>
public sealed class CommPolicy
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Basis { get; set; } = CommBases.Sales;
    public decimal RatePct { get; set; }
    public string RepScope { get; set; } = CommRepScopes.All;
    public string? RepCode { get; set; }
    public string ProductScope { get; set; } = CommProductScopes.All;
    public long? MaterialId { get; set; }
    public string? MaterialGroupCode { get; set; }
    public string CustomerScope { get; set; } = CommCustomerScopes.All;
    public long? PartyId { get; set; }
    public string? AreaCode { get; set; }
    public string? ValidFrom { get; set; }
    public string? ValidTo { get; set; }
    public bool IsActive { get; set; } = true;
    public List<CommPolicyTier> Tiers { get; set; } = new();
}

/// <summary>One slab tier of a policy: when the evaluated base reaches
/// <see cref="FromAmount"/>, this slab's rate applies (the slab with the
/// largest from_amount &lt;= base wins).</summary>
public sealed class CommPolicyTier
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PolicyId { get; set; }
    public decimal FromAmount { get; set; }
    public decimal RatePct { get; set; }
}

/// <summary>One generated commission row (sco_commission_entries).
/// Amounts are signed: return adjustments carry negative basis and
/// commission amounts. PeriodDate is the source event's date
/// (yyyy-MM-dd) and drives period settlement selection.</summary>
public sealed class CommEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PolicyId { get; set; }
    public string RepCode { get; set; } = string.Empty;
    public string SourceDocKind { get; set; } = string.Empty;
    public long SourceDocId { get; set; }
    public string? SourceDocNo { get; set; }
    public string? SourceRef { get; set; }
    public long? PartyId { get; set; }
    public string? PartyName { get; set; }
    public string Basis { get; set; } = CommBases.Sales;
    public decimal BasisAmount { get; set; }
    public decimal RatePct { get; set; }
    public decimal CommissionAmount { get; set; }
    public string Status { get; set; } = CommEntryStatuses.Pending;
    public string PeriodDate { get; set; } = string.Empty;
    public Guid? SettlementId { get; set; }
    public string? Notes { get; set; }
}

/// <summary>One per-rep period settlement (sco_commission_settlements).
/// TotalAmount is the sum of the Approved entries it captured; paying it
/// posts a journal voucher (Dr commission expense / Cr cash) and stamps
/// <see cref="JournalRef"/> with the journal document number.</summary>
public sealed class CommSettlement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RepCode { get; set; } = string.Empty;
    public string PeriodFrom { get; set; } = string.Empty;
    public string PeriodTo { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public string Status { get; set; } = CommSettlementStatuses.Draft;
    public string? JournalRef { get; set; }
    public int EntryCount { get; set; }
}

/// <summary>Commission persistence contract (migration 097). Declared
/// here rather than in Domain/Interfaces because the owner's file rule
/// for this phase allows exactly one new domain file; the shape follows
/// ICrmRepository: company keying is the party world's company CODE,
/// except the Generate* methods, which accept the document's own
/// company key (Guid or code) and resolve it internally.</summary>
public interface ICommRepository
{
    Task<IReadOnlyList<CommPolicy>> GetPoliciesAsync(string companyId, CancellationToken cancellationToken = default);
    Task SavePolicyAsync(string companyId, CommPolicy policy, string userId, CancellationToken cancellationToken = default);
    Task SoftDeletePolicyAsync(string companyId, Guid policyId, string userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CommEntry>> GetEntriesAsync(string companyId, string? repCode = null, string? status = null,
        string? periodFrom = null, string? periodTo = null, CancellationToken cancellationToken = default);
    Task<int> SetEntryStatusAsync(string companyId, Guid entryId, string fromStatus, string toStatus, string userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CommSettlement>> GetSettlementsAsync(string companyId, string? repCode = null, CancellationToken cancellationToken = default);
    Task<CommSettlement?> GetSettlementAsync(string companyId, Guid settlementId, CancellationToken cancellationToken = default);
    Task<CommSettlement> CreateSettlementAsync(string companyId, string repCode, string periodFrom, string periodTo, string userId, CancellationToken cancellationToken = default);
    Task<int> SetSettlementStatusAsync(string companyId, Guid settlementId, string fromStatus, string toStatus, string? journalRef, string userId, CancellationToken cancellationToken = default);
    Task<CommPaymentAccounts?> ResolvePaymentAccountsAsync(string companyId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<(string Code, string Name)>> GetRepOptionsAsync(string companyId, CancellationToken cancellationToken = default);

    Task<int> GenerateForInvoiceAsync(string companyKey, long docId, string userId, CancellationToken cancellationToken = default);
    Task<int> GenerateForReturnAsync(string companyKey, long returnDocId, string userId, CancellationToken cancellationToken = default);
    Task<int> GenerateCollectionForPeriodAsync(string companyId, string periodFrom, string periodTo, string userId, CancellationToken cancellationToken = default);
}

/// <summary>Pure policy resolution (spec section 9): among the ACTIVE
/// policies of the requested basis whose scopes all match, the most
/// specific wins — rep+material &gt; rep+group &gt; rep+party &gt;
/// rep-only &gt; company default. A policy matches when its rep scope is
/// All or the document's rep, its product scope is All / the line's
/// material / the line's material group, its customer scope is All or
/// the document's party, its territory is unset or the document's area,
/// and the document date falls inside its validity window. Ties keep
/// the earliest-loaded policy (policies load in creation order).</summary>
public static class CommPolicyResolver
{
    public static CommPolicy? Resolve(
        IReadOnlyList<CommPolicy> policies, string basis, string repCode,
        long? materialId, string? materialGroupCode, long? partyId,
        string? areaCode, string documentDate)
    {
        CommPolicy? best = null;
        var bestScore = -1;
        foreach (var policy in policies)
        {
            if (!policy.IsActive) continue;
            if (!string.Equals(policy.Basis, basis, StringComparison.OrdinalIgnoreCase)) continue;
            var score = MatchScore(policy, repCode, materialId, materialGroupCode, partyId, areaCode, documentDate);
            if (score > bestScore)
            {
                bestScore = score;
                best = policy;
            }
        }
        return best;
    }

    /// <summary>Specificity score of a policy for one document line, or
    /// -1 when the policy does not match at all.</summary>
    public static int MatchScore(
        CommPolicy policy, string repCode, long? materialId,
        string? materialGroupCode, long? partyId, string? areaCode, string documentDate)
    {
        var repSpecific = false;
        if (policy.RepScope == CommRepScopes.SpecificRep)
        {
            if (!string.Equals(policy.RepCode, repCode, StringComparison.OrdinalIgnoreCase))
                return -1;
            repSpecific = true;
        }

        var productScore = 0;
        if (policy.ProductScope == CommProductScopes.Material)
        {
            if (materialId is null || policy.MaterialId != materialId) return -1;
            productScore = 40;
        }
        else if (policy.ProductScope == CommProductScopes.Group)
        {
            if (string.IsNullOrWhiteSpace(materialGroupCode) ||
                !string.Equals(policy.MaterialGroupCode, materialGroupCode, StringComparison.OrdinalIgnoreCase))
                return -1;
            productScore = 30;
        }

        var partyScore = 0;
        if (policy.CustomerScope == CommCustomerScopes.SpecificParty)
        {
            if (partyId is null || policy.PartyId != partyId) return -1;
            partyScore = 20;
        }

        if (!string.IsNullOrWhiteSpace(policy.AreaCode) &&
            !string.Equals(policy.AreaCode, areaCode, StringComparison.OrdinalIgnoreCase))
            return -1;

        var date = documentDate.Length > 10 ? documentDate[..10] : documentDate;
        if (!string.IsNullOrWhiteSpace(policy.ValidFrom) &&
            string.CompareOrdinal(date, policy.ValidFrom.Length > 10 ? policy.ValidFrom[..10] : policy.ValidFrom) < 0)
            return -1;
        if (!string.IsNullOrWhiteSpace(policy.ValidTo) &&
            string.CompareOrdinal(date, policy.ValidTo.Length > 10 ? policy.ValidTo[..10] : policy.ValidTo) > 0)
            return -1;

        return productScore + partyScore + (repSpecific ? 5 : 0) + (string.IsNullOrWhiteSpace(policy.AreaCode) ? 0 : 1);
    }

    /// <summary>The rate a policy charges on <paramref name="slabBase"/>:
    /// the tier slab whose from_amount is the largest &lt;= the base
    /// overrides the policy's flat rate; no qualifying slab (or no
    /// tiers) leaves the flat rate.</summary>
    public static decimal ResolveRate(CommPolicy policy, decimal slabBase)
    {
        decimal? slabRate = null;
        var slabFrom = -1m;
        foreach (var tier in policy.Tiers)
        {
            if (tier.FromAmount <= slabBase && tier.FromAmount > slabFrom)
            {
                slabFrom = tier.FromAmount;
                slabRate = tier.RatePct;
            }
        }
        return slabRate ?? policy.RatePct;
    }
}

/// <summary>The accounts a settlement payment posts to, resolved from
/// the company's chart. NOTE on the mandate's premise: no rep-level
/// commission account column exists in the schema — migration 036's
/// commission_account_code belongs to payment_methods, and sales_reps
/// (031) carries only commission_pct — so the expense account is the
/// first postable chart account whose name carries "commission"
/// (resolved per company at payment time, SAL-7014 when the chart has
/// none). The credit account is the canonical Cash code
/// (GlAccounts.Cash). AccountCode is kept for the journal description /
/// display.</summary>
public sealed record CommPaymentAccounts(
    Guid CompanyGuid,
    Guid ExpenseAccountId,
    string ExpenseAccountCode,
    Guid CreditAccountId,
    string CreditAccountCode,
    string CurrencyCode);
