using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

/// <summary>
/// Persistence contract for the MasterCard module (ribbon completion pass):
/// cost centers (+ allocations), banks, currencies (+ dated rates), parties
/// (+ groups) and sales reps (+ groups). Implemented provider-neutrally
/// (SQLite + PostgreSQL) in Infrastructure. Every operation is company-scoped;
/// writes are single-transaction with an in-transaction uniqueness re-check,
/// and deletes are soft deletes guarded against ledger references.
/// </summary>
public interface IMasterCardRepository
{
    // ------------------------------------------------------------ Cost centers
    Task<IReadOnlyList<CostCenter>> GetCostCentersAsync(string companyId, CancellationToken cancellationToken = default);
    Task<CostCenter?> GetCostCenterByCodeAsync(string companyId, string code, CancellationToken cancellationToken = default);
    Task<CostCenter?> GetCostCenterByIdAsync(string companyId, Guid id, CancellationToken cancellationToken = default);
    Task SaveCostCenterAsync(string companyId, CostCenter costCenter, string userId, CancellationToken cancellationToken = default);
    Task SoftDeleteCostCenterAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default);
    Task<int> CountCostCenterJournalRefsAsync(string companyId, string code, CancellationToken cancellationToken = default);
    Task<LedgerBalance> GetCostCenterBalanceAsync(string companyId, string code, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, LedgerBalance>> GetCostCenterBalancesAsync(string companyId, CancellationToken cancellationToken = default);

    // ------------------------------------------------- Cost center allocations
    Task<IReadOnlyList<CostCenterAllocation>> GetAllocationsAsync(string companyId, CancellationToken cancellationToken = default);
    Task SaveAllocationAsync(string companyId, CostCenterAllocation allocation, string userId, CancellationToken cancellationToken = default);
    Task SoftDeleteAllocationAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default);

    // ------------------------------------------------------------------- Banks
    Task<IReadOnlyList<Bank>> GetBanksAsync(string companyId, CancellationToken cancellationToken = default);
    Task<Bank?> GetBankByCodeAsync(string companyId, string code, CancellationToken cancellationToken = default);
    Task SaveBankAsync(string companyId, Bank bank, string userId, CancellationToken cancellationToken = default);
    Task SoftDeleteBankAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default);
    Task<LedgerBalance> GetBankBalanceAsync(string companyId, string glAccountCode, CancellationToken cancellationToken = default);

    // -------------------------------------------------------------- Currencies
    Task<IReadOnlyList<Currency>> GetCurrenciesAsync(string companyId, CancellationToken cancellationToken = default);
    Task<Currency?> GetCurrencyByCodeAsync(string companyId, string code, CancellationToken cancellationToken = default);
    Task SaveCurrencyAsync(string companyId, Currency currency, string userId, CancellationToken cancellationToken = default);
    Task SoftDeleteCurrencyAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CurrencyRate>> GetRatesAsync(string companyId, string currencyCode, CancellationToken cancellationToken = default);
    Task SaveRateAsync(string companyId, CurrencyRate rate, string userId, CancellationToken cancellationToken = default);

    // ----------------------------------------------------------------- Parties
    Task<IReadOnlyList<Party>> GetPartiesAsync(string companyId, string? kind = null, CancellationToken cancellationToken = default);
    Task<Party?> GetPartyByCodeAsync(string companyId, string kind, string code, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Party>> SearchPartiesAsync(string companyId, string? kind, string query, CancellationToken cancellationToken = default);
    Task SavePartyAsync(string companyId, Party party, string userId, CancellationToken cancellationToken = default);
    Task SoftDeletePartyAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default);
    Task<int> CountPartyLedgerRefsAsync(string companyId, string kind, string code, CancellationToken cancellationToken = default);
    Task<LedgerBalance> GetPartyBalanceAsync(string companyId, string kind, string code, CancellationToken cancellationToken = default);

    // ------------------------------------------------------------ Party groups
    Task<IReadOnlyList<PartyGroup>> GetPartyGroupsAsync(string companyId, string? kind = null, CancellationToken cancellationToken = default);
    Task<PartyGroup?> GetPartyGroupByCodeAsync(string companyId, string kind, string code, CancellationToken cancellationToken = default);
    Task SavePartyGroupAsync(string companyId, PartyGroup group, string userId, CancellationToken cancellationToken = default);
    Task SoftDeletePartyGroupAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default);

    // -------------------------------------------------------------- Sales reps
    Task<IReadOnlyList<SalesRep>> GetSalesRepsAsync(string companyId, CancellationToken cancellationToken = default);
    Task<SalesRep?> GetSalesRepByCodeAsync(string companyId, string code, CancellationToken cancellationToken = default);
    Task SaveSalesRepAsync(string companyId, SalesRep rep, string userId, CancellationToken cancellationToken = default);
    Task SoftDeleteSalesRepAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default);

    // -------------------------------------------------------- Sales rep groups
    Task<IReadOnlyList<SalesRepGroup>> GetSalesRepGroupsAsync(string companyId, CancellationToken cancellationToken = default);
    Task<SalesRepGroup?> GetSalesRepGroupByCodeAsync(string companyId, string code, CancellationToken cancellationToken = default);
    Task SaveSalesRepGroupAsync(string companyId, SalesRepGroup group, string userId, CancellationToken cancellationToken = default);
    Task SoftDeleteSalesRepGroupAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default);
}
