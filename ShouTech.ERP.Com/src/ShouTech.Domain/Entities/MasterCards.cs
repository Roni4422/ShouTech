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
