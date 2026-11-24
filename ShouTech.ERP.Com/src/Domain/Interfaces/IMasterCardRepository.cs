using ShouTech.Domain.Entities;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.Domain.Entities.Navigation;
using ShouTech.Domain.Enums;

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
    Task<string> NextPartyCodeAsync(string companyId, string kind, CancellationToken cancellationToken = default);
    Task SoftDeletePartyAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default);
    Task<int> CountPartyLedgerRefsAsync(string companyId, string kind, string code, CancellationToken cancellationToken = default);
    Task<LedgerBalance> GetPartyBalanceAsync(string companyId, string kind, string code, CancellationToken cancellationToken = default);

    // ------------------------------------------- Party structure (W1, mig 095)
    Task<IReadOnlyList<PartyContact>> GetPartyContactsAsync(string companyId, Guid partyId, CancellationToken cancellationToken = default);
    Task SavePartyContactAsync(string companyId, PartyContact contact, string userId, CancellationToken cancellationToken = default);
    Task SoftDeletePartyContactAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PartyAddress>> GetPartyAddressesAsync(string companyId, Guid partyId, CancellationToken cancellationToken = default);
    Task SavePartyAddressAsync(string companyId, PartyAddress address, string userId, CancellationToken cancellationToken = default);
    Task SoftDeletePartyAddressAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PartyBranch>> GetPartyBranchesAsync(string companyId, Guid partyId, CancellationToken cancellationToken = default);
    Task SavePartyBranchAsync(string companyId, PartyBranch branch, string userId, CancellationToken cancellationToken = default);
    Task SoftDeletePartyBranchAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default);

    /// <summary>Posts the party's ONE opening balance (GL journal Dr/Cr
    /// party control vs retained earnings 32 + the party_ledger row +
    /// the marker on the party) in a single transaction. A second
    /// opening while the marker exists is refused.</summary>
    Task<string> PostPartyOpeningBalanceAsync(string companyId, string kind, string code, decimal signedAmount, DateOnly openingDate, string currencyCode, string userId, CancellationToken cancellationToken = default);

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

// ---- merged from src/Domain/Interfaces/IDefinitionRepository.cs ----
/// <summary>
/// Persistence contract for the Definition module (ribbon completion F1).
/// Generic definition-set CRUD is driven entirely by <see cref="DefSet"/>
/// descriptors from the static registry; SQL identifiers come only from those
/// descriptors (allow-list), values are always parameterized, and every
/// statement is company-scoped. Dedicated operations cover installments
/// (schedule generation + collection posting to party_ledger), loyalty
/// points, and the delivery desk — each in a single transaction.
/// Implemented provider-neutrally (SQLite + PostgreSQL) in Infrastructure.
/// </summary>
public interface IDefinitionRepository
{
    // ------------------------------------------------------------ generic sets
    Task<IReadOnlyList<DefRow>> ListAsync(DefSet set, string companyId, CancellationToken cancellationToken = default);

    /// <summary>Insert (id null/blank) or update a row; returns the row id. Uniqueness is re-checked inside the transaction.</summary>
    Task<string> SaveAsync(DefSet set, string companyId, string? id, IReadOnlyDictionary<string, object?> values, string actor, CancellationToken cancellationToken = default);

    Task SoftDeleteAsync(DefSet set, string companyId, string id, string actor, CancellationToken cancellationToken = default);
    Task SetActiveAsync(DefSet set, string companyId, string id, bool isActive, string actor, CancellationToken cancellationToken = default);

    // ------------------------------------------------------------ installments
    Task<string> CreateInstallmentAsync(string companyId, InstallmentPlan plan, IReadOnlyList<InstallmentScheduleRow> schedule, string actor, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InstallmentPlan>> ListInstallmentsAsync(string companyId, string? partyCode, string? status, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InstallmentScheduleRow>> ListScheduleAsync(string companyId, string installmentId, CancellationToken cancellationToken = default);
    Task SoftDeleteInstallmentAsync(string companyId, string installmentId, string actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a collection against one schedule row AND inserts the matching
    /// party_ledger credit (party_kind 'C', doc_kind 'INS') in the same
    /// transaction. Returns the updated schedule row.
    /// </summary>
    Task<InstallmentScheduleRow> CollectInstallmentAsync(string companyId, string scheduleId, decimal amount, DateOnly paidDate, string actor, CancellationToken cancellationToken = default);

    // ----------------------------------------------------------------- loyalty
    Task<IReadOnlyList<LoyaltyAccount>> ListLoyaltyAccountsAsync(string companyId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LoyaltyEntry>> ListLoyaltyEntriesAsync(string companyId, string accountId, CancellationToken cancellationToken = default);

    /// <summary>Applies a signed points delta: writes the entry and updates the balance atomically; negative results are rejected.</summary>
    Task<decimal> AdjustLoyaltyAsync(string companyId, string partyCode, decimal pointsDelta, string? docRef, string? notes, string actor, CancellationToken cancellationToken = default);

    // ---------------------------------------------------------------- delivery
    Task<IReadOnlyList<DeliveryRequest>> ListDeliveryRequestsAsync(string companyId, CancellationToken cancellationToken = default);
    Task<string> SaveDeliveryRequestAsync(string companyId, DeliveryRequest request, string actor, CancellationToken cancellationToken = default);
    Task SetDeliveryStatusAsync(string companyId, string requestId, string newStatus, string? driverCode, string actor, CancellationToken cancellationToken = default, string? receivedBy = null, byte[]? signatureImage = null);
    Task SoftDeleteDeliveryRequestAsync(string companyId, string requestId, string actor, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DriverSettlement>> ListDriverSettlementsAsync(string companyId, CancellationToken cancellationToken = default);

    /// <summary>Creates a Closed driver cash settlement (net = collected - fees) in one transaction.</summary>
    Task<string> CloseDriverCashAsync(string companyId, string driverCode, DateOnly settleDate, decimal collectedAmount, decimal feesAmount, string actor, CancellationToken cancellationToken = default);
}

// ---- merged from src/Domain/Interfaces/IAssetRepository.cs ----
public interface IAssetRepository
{
    Task<IReadOnlyList<FixedAsset>> GetAllAsync(string companyId, CancellationToken cancellationToken = default);
    Task SaveAsync(string companyId, FixedAsset asset, string userId, CancellationToken cancellationToken = default);
    Task SoftDeleteAsync(string companyId, Guid assetId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Posts the not-yet-posted accumulated depreciation of an
    /// asset to the GL (Dr depreciation expense / Cr accumulated
    /// depreciation) in one transaction and returns the journal document
    /// number, or null when nothing remains to post (Step 3, AST-01).</summary>
    Task<string?> PostDepreciationAsync(string companyId, Guid assetId, decimal accumulatedDepreciation, DateOnly asOf, string userId, CancellationToken cancellationToken = default);
}

// ---- merged from src/Domain/Interfaces/IProjectRepository.cs ----
public interface IProjectRepository
{
    Task<IReadOnlyList<Project>> GetAllAsync(string companyId, CancellationToken cancellationToken = default);
    Task SaveAsync(string companyId, Project project, string userId, CancellationToken cancellationToken = default);
    Task SoftDeleteAsync(string companyId, Guid projectId, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectTask>> GetTasksAsync(string companyId, Guid projectId, CancellationToken cancellationToken = default);
    Task SaveTaskAsync(string companyId, ProjectTask task, string userId, CancellationToken cancellationToken = default);
    Task SoftDeleteTaskAsync(string companyId, Guid taskId, string userId, CancellationToken cancellationToken = default);
}

// ---- merged from src/Domain/Interfaces/IFYearRepo.cs ----
public interface IFYearRepo
{
    Task<FYear?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<FYear>> GetByCompanyAsync(Guid companyId, CancellationToken ct = default);
    Task AddAsync(FYear year, CancellationToken ct = default);
    Task UpdateAsync(FYear year, CancellationToken ct = default);
    Task ClearCurrentForCompanyAsync(Guid companyId, Guid modifiedBy, CancellationToken ct = default);
}

// ---- merged from src/Domain/Interfaces/ICompanyRepository.cs ----
    public interface ICompanyRepository
    {
        Task<List<Comp>> GetAllAsync();
        Task<Comp?> GetByCodeAsync(string code);
        Task<bool> HasCompaniesAsync();
        Task AddAsync(Comp Comp);
    }

// ---- merged from src/Domain/Interfaces/IComRepo.cs ----
public interface IComRepo1
{
    Task AddAsync(Comp company, CancellationToken cancellationToken = default);
    Task<bool> CodeExistsAsync(string code, Guid? excludeId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Comp>> GetAllAsync(bool activeOnly = true, CancellationToken cancellationToken = default);
    Task<Comp?> GetByCodeAsync(string code, bool includeDeleted = false, CancellationToken cancellationToken = default);
    Task<Comp?> GetByIdAsync(Guid id, bool includeDeleted = false, CancellationToken cancellationToken = default);
    Task SoftDeleteAsync(Guid id, Guid deletedBy, CancellationToken cancellationToken = default);
    Task UpdateAsync(Comp company, CancellationToken cancellationToken = default);
}

public interface IComRepo
{
    Task<Comp?> GetByIdAsync(Guid id, bool includeDeleted = false, CancellationToken cancellationToken = default);
    Task<Comp?> GetByCodeAsync(string code, bool includeDeleted = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Comp>> GetAllAsync(bool activeOnly = true, CancellationToken cancellationToken = default);
    Task AddAsync(Comp company, CancellationToken cancellationToken = default);
    Task UpdateAsync(Comp company, CancellationToken cancellationToken = default);
    Task SoftDeleteAsync(Guid id, Guid deletedBy, CancellationToken cancellationToken = default);
    Task<bool> CodeExistsAsync(string code, Guid? excludeId = null, CancellationToken cancellationToken = default);
}

// ---- merged from src/Domain/Interfaces/IUserRepository.cs ----
    public interface IUserRepository
    {
        Task<Usr?> GetByUsernameAsync(string username);
        Task<bool> ValidateCredentialsAsync(string username, string passwordHash);
    }

// ---- merged from src/Domain/Interfaces/IUsrRepo.cs ----
public interface IUsrRepo
{
    Task<Usr?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Usr?> GetByUserNameAsync(string userName, CancellationToken ct = default);
    Task AddAsync(Usr user, CancellationToken ct = default);
    Task UpdateAsync(Usr user, CancellationToken ct = default);
}

// ---- merged from src/Domain/Interfaces/IDatabaseHealth.cs ----
public sealed record DatabaseHealthResult(bool IsAvailable, string Provider, string Message, TimeSpan? Latency);

public interface IDatabaseHealth
{
    Task<DatabaseHealthResult> CheckAsync(CancellationToken cancellationToken = default);
}

// ---- merged from src/Domain/Interfaces/INavigationRepository.cs ----
public interface INavigationRepository
{
    Task<IReadOnlyList<MainMenuItem>> GetMainMenuItemsAsync(
        int userId,
        int tenantId = 1,
        int companyId = 1,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RibbonLayoutRow>> GetRibbonLayoutAsync(
        int userId,
        string? tabCode = null,
        int tenantId = 1,
        int companyId = 1,
        CancellationToken cancellationToken = default);
}

// ---- merged from src/Domain/Interfaces/ILicProv.cs ----
public interface ILicProv
{
    /// <summary>
    /// Full validation against native engine (dongle + signature + hardware + expiry).
    /// </summary>
    Task<LicResult> ValidateAsync(CancellationToken ct = default);

    /// <summary>
    /// Fast check after last validation (cached).
    /// </summary>
    LicEd CurrentEdition { get; }

    /// <summary>
    /// Returns true if the module is enabled in the current license.
    /// Core modules return true for any valid Core/Demo license.
    /// </summary>
    bool IsModuleEnabled(string moduleKey);

    /// <summary>
    /// Returns true if the current license allows write operations
    /// (i.e. not Viewer and not expired beyond grace).
    /// </summary>
    bool CanWrite { get; }

    Task<LicResult> RefreshAsync(CancellationToken ct = default);
}

public sealed class LicResult
{
    public required LicStat Status { get; init; }
    public required LicEd Edition { get; init; }

    public DateTime? ExpiresAt { get; init; }
    public DateTime? GracePeriodEndsAt { get; init; }
    public int? MaxDevices { get; init; }
    public int? ActiveDevices { get; init; }
    public string? Message { get; init; }

    /// <summary>
    /// Enabled modules snapshot (from signed license payload).
    /// </summary>
    public IReadOnlySet<string> Modules { get; init; } = new HashSet<string>();

    public bool IsUsable =>
        Status is LicStat.Valid or LicStat.GracePeriod;

    public bool IsViewer => Edition == LicEd.Viewer;
    public bool IsDemo => Edition == LicEd.Demo;
    public bool IsCoreOrAbove => Edition >= LicEd.Core || Edition == LicEd.Demo;
}
