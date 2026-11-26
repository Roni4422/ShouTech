namespace ShouTech.Domain.Entities;

/// <summary>Contract lifecycle (owner sales spec section 10, migration
/// 101). Draft is editable; only Active contracts price, validate
/// orders and generate scheduled deliveries; Suspended pauses an
/// active contract; Expired is stamped by the status sweep when the
/// validity window passes; Completed when every line is fully
/// invoiced; Renewed is terminal for a contract replaced by a
/// successor (the successor carries renewal_of_id back to it).</summary>
public static class ContractStatuses
{
    public const string Draft = "Draft";
    public const string Active = "Active";
    public const string Suspended = "Suspended";
    public const string Expired = "Expired";
    public const string Completed = "Completed";
    public const string Renewed = "Renewed";

    public static readonly IReadOnlyList<string> All =
        new[] { Draft, Active, Suspended, Expired, Completed, Renewed };
}

/// <summary>One sales contract header (sct_contracts). Dates are
/// yyyy-MM-dd text, the document-world convention. Party is the party
/// ledger identity documents carry (party_id + optional code/name
/// snapshots).</summary>
public sealed class SalesContract
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ContractNo { get; set; } = string.Empty;
    public long? PartyId { get; set; }
    public string? PartyCode { get; set; }
    public string? PartyName { get; set; }
    public string Title { get; set; } = string.Empty;
    public string StartDate { get; set; } = string.Empty;
    public string EndDate { get; set; } = string.Empty;
    public string Status { get; set; } = ContractStatuses.Draft;
    public string CurrencyCode { get; set; } = "SAR";
    public string? PaymentTerms { get; set; }
    public string? GuaranteeText { get; set; }
    public decimal GuaranteeAmount { get; set; }
    public Guid? RenewalOfId { get; set; }
    public string? Notes { get; set; }
    public List<SalesContractLine> Lines { get; set; } = new();
}

/// <summary>One contracted material line (sct_contract_lines).
/// DeliveredQty / InvoicedQty are STORED progress maintained by the
/// repository refresh from posted chain documents (the per_delivered /
/// per_billed pattern); RemainingQty is computed, never stored. The
/// cadence fields drive scheduled generation: every IntervalDays from
/// NextDeliveryDate, ScheduledQty is due (capped at the remaining
/// contracted quantity).</summary>
public sealed class SalesContractLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ContractId { get; set; }
    public int LineNo { get; set; }
    public long MaterialId { get; set; }
    public string? MaterialCode { get; set; }
    public string? Description { get; set; }
    public string? Unit { get; set; }
    public decimal ContractedQty { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DeliveredQty { get; set; }
    public decimal InvoicedQty { get; set; }
    public string? NextDeliveryDate { get; set; }
    public int? IntervalDays { get; set; }
    public decimal ScheduledQty { get; set; }

    public decimal RemainingQty => Math.Max(0m, ContractedQty - Math.Max(DeliveredQty, InvoicedQty));
    public decimal OrderedRemainingQty(decimal orderedQty) => ContractedQty - orderedQty;
}

/// <summary>One status-history row (sct_contract_events).</summary>
public sealed class SalesContractEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ContractId { get; set; }
    public string EventKind { get; set; } = string.Empty;
    public string? FromStatus { get; set; }
    public string? ToStatus { get; set; }
    public string? Detail { get; set; }
    public string? CreatedBy { get; set; }
    public string? CreatedAt { get; set; }
}

/// <summary>Fulfillment + profitability of one contract (spec section
/// 10): contracted vs delivered vs invoiced quantities and amounts,
/// remaining quantity, invoiced revenue and the COGS the posted linked
/// invoices carried (valued stock movements, the W7b profit pattern),
/// so Margin = Revenue - Cogs.</summary>
public sealed class ContractFulfillment
{
    public Guid ContractId { get; set; }
    public string ContractNo { get; set; } = string.Empty;
    public decimal ContractedQty { get; set; }
    public decimal DeliveredQty { get; set; }
    public decimal InvoicedQty { get; set; }
    public decimal RemainingQty { get; set; }
    public decimal ContractedAmount { get; set; }
    public decimal DeliveredAmount { get; set; }
    public decimal InvoicedAmount { get; set; }
    public decimal Revenue { get; set; }
    public decimal Cogs { get; set; }
    public decimal Margin => Revenue - Cogs;
    public decimal MarginPct => Revenue > 0m ? decimal.Round(100m * (Revenue - Cogs) / Revenue, 2) : 0m;
}

/// <summary>Contract persistence contract (migration 101). Declared
/// here with the entities, the ICommRepository pattern. Company keying
/// is the party world's company CODE, except the chain-facing methods
/// (ValidateOrder / progress refresh), which accept the document's own
/// company key (Guid or code) and resolve it internally.</summary>
public interface IContractRepository
{
    Task<IReadOnlyList<SalesContract>> GetContractsAsync(string companyId, string? status = null, CancellationToken cancellationToken = default);
    Task<SalesContract?> GetContractAsync(string companyId, Guid contractId, CancellationToken cancellationToken = default);
    Task SaveContractAsync(string companyId, SalesContract contract, string userId, CancellationToken cancellationToken = default);
    Task SoftDeleteContractAsync(string companyId, Guid contractId, string userId, CancellationToken cancellationToken = default);
    Task SetStatusAsync(string companyId, Guid contractId, string toStatus, string userId, CancellationToken cancellationToken = default);
    Task<SalesContract> RenewAsync(string companyId, Guid contractId, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SalesContract>> GetExpiringAsync(string companyId, int withinDays, string asOfDate, CancellationToken cancellationToken = default);
    Task<int> ExpireOverdueAsync(string companyId, string asOfDate, string userId, CancellationToken cancellationToken = default);
    Task<ContractFulfillment?> GetFulfillmentAsync(string companyId, Guid contractId, CancellationToken cancellationToken = default);
    Task<int> GenerateDueDeliveriesAsync(string companyKey, string asOfDate, string userId, CancellationToken cancellationToken = default);
    Task RefreshProgressAsync(string companyKey, Guid contractId, CancellationToken cancellationToken = default);
}
