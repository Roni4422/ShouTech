namespace ShouTech.Domain.Entities;

/// <summary>Party kinds stored in <c>parties.kind</c> and <c>party_ledger.party_kind</c>.</summary>
public static class PartyKinds
{
    public const string Customer = "C";
    public const string Supplier = "S";

    public static readonly IReadOnlyList<string> All = new[] { Customer, Supplier };

    public static bool IsValid(string? kind) => kind is Customer or Supplier;
}

/// <summary>Debit/credit totals read from a ledger source (journal lines or party ledger).</summary>
public sealed class LedgerBalance
{
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal Balance => Debit - Credit;
}

/// <summary>
/// A node in the per-company cost-center tree. Only non-group (leaf) centers
/// are postable; group centers exist to organize the tree and roll balances up.
/// The level is derived from the parent chain when the row is saved.
/// </summary>
public sealed class CostCenter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public Guid? ParentId { get; set; }
    public bool IsGroup { get; set; }
    public int Level { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
}

/// <summary>
/// ERPNext-style cost-center allocation: activity posted to the main center is
/// distributed to the line centers by percentage (lines must total 100%).
/// Posting-time application is handled by the journal engine; this entity is
/// the stored, validated rule.
/// </summary>
public sealed class CostCenterAllocation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MainCostCenterId { get; set; }
    public DateOnly ValidFrom { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public bool IsActive { get; set; } = true;
    public List<CostCenterAllocationLine> Lines { get; set; } = new();
}

/// <summary>One distribution row of a <see cref="CostCenterAllocation"/>.</summary>
public sealed class CostCenterAllocationLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AllocationId { get; set; }
    public Guid CostCenterId { get; set; }
    public decimal Percentage { get; set; }
}

/// <summary>A bank account master card; its live balance comes from the linked GL account.</summary>
public sealed class Bank
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public string? BranchName { get; set; }
    public string? AccountNo { get; set; }
    public string? Iban { get; set; }
    public string CurrencyCode { get; set; } = "SAR";
    public string? GlAccountCode { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
}

/// <summary>A currency master card (ISO-style 3-letter code).</summary>
public sealed class Currency
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public string? Symbol { get; set; }
    public string? FractionName { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>One dated exchange-rate row for a currency, with its allowed min/max band.</summary>
public sealed class CurrencyRate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CurrencyCode { get; set; } = string.Empty;
    public DateOnly RateDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public decimal Rate { get; set; }
    public decimal? MinRate { get; set; }
    public decimal? MaxRate { get; set; }
}

/// <summary>A customer (<see cref="PartyKinds.Customer"/>) or supplier (<see cref="PartyKinds.Supplier"/>) master card.</summary>
public sealed class Party
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Stable integer identity in the operational stores (Fix Phase Step 4,
    /// migration 061): <c>party_ledger.party_id</c>, document
    /// <c>party_id</c> and <c>payment_allocations.party_id</c> all carry this
    /// value. Assigned by the repository on insert (legacy ids were kept by
    /// the migration); 0 means "not yet persisted".
    /// </summary>
    public long LedgerId { get; set; }
    public string Kind { get; set; } = PartyKinds.Customer;
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public Guid? PartyGroupId { get; set; }
    public string? TaxNo { get; set; }
    public string? CommercialRegNo { get; set; }
    public decimal CreditLimit { get; set; }
    public int CreditDays { get; set; } = 30;
    public string? PriceTier { get; set; }
    public string? GlAccountCode { get; set; }
    public string? Phone { get; set; }
    public string? Mobile { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }

    // W1 (migration 095): per-customer currency + risk classification,
    // the blacklist/suspension model, and the opening-balance marker.
    /// <summary>Default document currency for this party (3-letter, nullable).</summary>
    public string? DefaultCurrency { get; set; }
    /// <summary>Risk classification: Low / Medium / High (nullable).</summary>
    public string? RiskClass { get; set; }
    public bool IsBlacklisted { get; set; }
    public string? BlacklistReason { get; set; }
    public string? BlacklistedAt { get; set; }
    public string? BlacklistedBy { get; set; }
    /// <summary>Signed opening balance as posted (debit positive for a
    /// customer receivable; the marker exists iff an opening entry was
    /// posted — a second opening is blocked while it is set).</summary>
    public decimal? OpeningBalanceAmount { get; set; }
    public string? OpeningBalanceDate { get; set; }
    public string? OpeningJournalId { get; set; }
}

/// <summary>Risk classes stored in <c>parties.risk_class</c> (migration 095).</summary>
public static class PartyRiskClasses
{
    public const string Low = "Low";
    public const string Medium = "Medium";
    public const string High = "High";

    public static readonly IReadOnlyList<string> All = new[] { Low, Medium, High };

    public static bool IsValid(string? value) => value is Low or Medium or High;
}

/// <summary>Address kinds stored in <c>party_addresses.kind</c> (migration 095).</summary>
public static class PartyAddressKinds
{
    public const string Delivery = "Delivery";
    public const string Billing = "Billing";
    public const string Other = "Other";

    public static readonly IReadOnlyList<string> All = new[] { Delivery, Billing, Other };

    public static bool IsValid(string? value) => value is Delivery or Billing or Other;
}

/// <summary>A named contact of a party (migration 095). The primary
/// contact mirrors to the party row's phone/mobile/email summary.</summary>
public sealed class PartyContact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PartyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Role { get; set; }
    public string? Phone { get; set; }
    public string? Mobile { get; set; }
    public string? Email { get; set; }
    public bool IsPrimary { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
}

/// <summary>One address of a party (migration 095). The default address
/// mirrors to the party row's address summary.</summary>
public sealed class PartyAddress
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PartyId { get; set; }
    public string Kind { get; set; } = PartyAddressKinds.Delivery;
    public string? City { get; set; }
    public string? Country { get; set; }
    public string? PostalCode { get; set; }
    public string? Address { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
}

/// <summary>A branch of a party (migration 095).</summary>
public sealed class PartyBranch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PartyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
}

/// <summary>A customer/supplier group (tree node) supplying defaults to its parties.</summary>
public sealed class PartyGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = PartyKinds.Customer;
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public Guid? ParentId { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A sales representative master card.</summary>
public sealed class SalesRep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public string? Phone { get; set; }
    public decimal CommissionPct { get; set; }
    public decimal TargetAmount { get; set; }
    public Guid? GroupId { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A sales-representative group.</summary>
public sealed class SalesRepGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public bool IsActive { get; set; } = true;
}
