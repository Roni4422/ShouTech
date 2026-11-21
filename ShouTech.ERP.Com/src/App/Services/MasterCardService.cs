using System.Globalization;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>
/// Master-data cards service (cost centers, banks, currencies, parties, sales
/// reps). All invariants are validated here before the repository is touched;
/// the repository re-checks uniqueness, parentage and delete guards inside its
/// transactions as a second line of defence.
/// </summary>
public sealed class MasterCardService
{
    private readonly IMasterCardRepository _repository;

    public MasterCardService(IMasterCardRepository repository)
        => _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    internal IMasterCardRepository Repository => _repository;

    // ------------------------------------------------------------ Cost centers

    public Task<IReadOnlyList<CostCenter>> GetCostCentersAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetCostCentersAsync(RequireCompany(companyId), cancellationToken);

    public Task<CostCenter?> GetCostCenterByCodeAsync(string companyId, string code, CancellationToken cancellationToken = default)
        => _repository.GetCostCenterByCodeAsync(RequireCompany(companyId), RequireCode(code, "Cost center"), cancellationToken);

    public async Task<Guid> SaveCostCenterAsync(string companyId, CostCenter costCenter, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(costCenter);
        RequireCode(costCenter.Code, "Cost center");
        RequireName(costCenter.NameEn, "Cost center");
        if (costCenter.ParentId.HasValue)
        {
            if (costCenter.ParentId.Value == costCenter.Id)
                throw new InvalidOperationException("A cost center cannot be its own parent.");
            var parent = await _repository.GetCostCenterByIdAsync(companyId, costCenter.ParentId.Value, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The parent cost center was not found for this company.");
            if (!parent.IsGroup)
                throw new InvalidOperationException("Only a group cost center can be a parent.");
        }
        costCenter.Code = costCenter.Code.Trim();
        costCenter.NameEn = costCenter.NameEn.Trim();
        await _repository.SaveCostCenterAsync(companyId, costCenter, RequireUser(userId), cancellationToken).ConfigureAwait(false);
        return costCenter.Id;
    }

    public async Task DeleteCostCenterAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (id == Guid.Empty)
            throw new ArgumentException("Cost center is required.", nameof(id));
        var existing = await _repository.GetCostCenterByIdAsync(companyId, id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Cost center was not found for this company.");
        var refs = await _repository.CountCostCenterJournalRefsAsync(companyId, existing.Code, cancellationToken).ConfigureAwait(false);
        if (refs > 0)
            throw new InvalidOperationException($"Cost center '{existing.Code}' cannot be deleted because {refs} journal line(s) reference it. Deactivate it instead.");
        await _repository.SoftDeleteCostCenterAsync(companyId, id, RequireUser(userId), cancellationToken).ConfigureAwait(false);
    }

    public Task<LedgerBalance> GetCostCenterBalanceAsync(string companyId, string code, CancellationToken cancellationToken = default)
        => _repository.GetCostCenterBalanceAsync(RequireCompany(companyId), RequireCode(code, "Cost center"), cancellationToken);

    public Task<IReadOnlyDictionary<string, LedgerBalance>> GetCostCenterBalancesAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetCostCenterBalancesAsync(RequireCompany(companyId), cancellationToken);

    // ------------------------------------------------- Cost center allocations

    public Task<IReadOnlyList<CostCenterAllocation>> GetAllocationsAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetAllocationsAsync(RequireCompany(companyId), cancellationToken);

    public async Task<Guid> SaveAllocationAsync(string companyId, CostCenterAllocation allocation, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(allocation);
        if (allocation.MainCostCenterId == Guid.Empty)
            throw new InvalidOperationException("An allocation must name its main cost center.");
        if (allocation.Lines is null || allocation.Lines.Count == 0)
            throw new InvalidOperationException("An allocation needs at least one distribution line.");
        var seen = new HashSet<Guid>();
        var total = 0m;
        foreach (var line in allocation.Lines)
        {
            if (line.CostCenterId == Guid.Empty)
                throw new InvalidOperationException("Every allocation line must name a cost center.");
            if (line.CostCenterId == allocation.MainCostCenterId)
                throw new InvalidOperationException("An allocation line cannot distribute to the main cost center itself.");
            if (!seen.Add(line.CostCenterId))
                throw new InvalidOperationException("An allocation cannot list the same cost center twice.");
            if (line.Percentage <= 0m || line.Percentage > 100m)
                throw new InvalidOperationException("Allocation percentages must be greater than 0 and at most 100.");
            total += line.Percentage;
        }
        if (Math.Abs(total - 100m) > 0.001m)
            throw new InvalidOperationException($"Allocation percentages must total 100% (currently {total.ToString("0.###", CultureInfo.InvariantCulture)}%).");
        await _repository.SaveAllocationAsync(companyId, allocation, RequireUser(userId), cancellationToken).ConfigureAwait(false);
        return allocation.Id;
    }

    public Task DeleteAllocationAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Allocation is required.", nameof(id));
        return _repository.SoftDeleteAllocationAsync(RequireCompany(companyId), id, RequireUser(userId), cancellationToken);
    }

    // ------------------------------------------------------------------- Banks

    public Task<IReadOnlyList<Bank>> GetBanksAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetBanksAsync(RequireCompany(companyId), cancellationToken);

    public Task<Bank?> GetBankByCodeAsync(string companyId, string code, CancellationToken cancellationToken = default)
        => _repository.GetBankByCodeAsync(RequireCompany(companyId), RequireCode(code, "Bank"), cancellationToken);

    public async Task<Guid> SaveBankAsync(string companyId, Bank bank, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(bank);
        RequireCode(bank.Code, "Bank");
        RequireName(bank.NameEn, "Bank");
        bank.CurrencyCode = NormalizeCurrencyCode(bank.CurrencyCode, "Bank currency");
        if (!string.IsNullOrWhiteSpace(bank.Iban))
        {
            var iban = bank.Iban.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
            if (iban.Length is < 15 or > 34 || !iban.All(char.IsLetterOrDigit))
                throw new InvalidOperationException("IBAN must be 15-34 letters or digits.");
            bank.Iban = iban;
        }
        bank.Code = bank.Code.Trim();
        bank.NameEn = bank.NameEn.Trim();
        await _repository.SaveBankAsync(companyId, bank, RequireUser(userId), cancellationToken).ConfigureAwait(false);
        return bank.Id;
    }

    public Task DeleteBankAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Bank is required.", nameof(id));
        return _repository.SoftDeleteBankAsync(RequireCompany(companyId), id, RequireUser(userId), cancellationToken);
    }

    public Task<LedgerBalance> GetBankBalanceAsync(string companyId, string glAccountCode, CancellationToken cancellationToken = default)
        => _repository.GetBankBalanceAsync(RequireCompany(companyId), glAccountCode ?? string.Empty, cancellationToken);

    // -------------------------------------------------------------- Currencies

    public Task<IReadOnlyList<Currency>> GetCurrenciesAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetCurrenciesAsync(RequireCompany(companyId), cancellationToken);

    public Task<Currency?> GetCurrencyByCodeAsync(string companyId, string code, CancellationToken cancellationToken = default)
        => _repository.GetCurrencyByCodeAsync(RequireCompany(companyId), RequireCode(code, "Currency"), cancellationToken);

    public async Task<Guid> SaveCurrencyAsync(string companyId, Currency currency, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(currency);
        currency.Code = NormalizeCurrencyCode(currency.Code, "Currency");
        RequireName(currency.NameEn, "Currency");
        currency.NameEn = currency.NameEn.Trim();
        await _repository.SaveCurrencyAsync(companyId, currency, RequireUser(userId), cancellationToken).ConfigureAwait(false);
        return currency.Id;
    }

    public Task DeleteCurrencyAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Currency is required.", nameof(id));
        return _repository.SoftDeleteCurrencyAsync(RequireCompany(companyId), id, RequireUser(userId), cancellationToken);
    }

    public Task<IReadOnlyList<CurrencyRate>> GetRatesAsync(string companyId, string currencyCode, CancellationToken cancellationToken = default)
        => _repository.GetRatesAsync(RequireCompany(companyId), NormalizeCurrencyCode(currencyCode, "Currency"), cancellationToken);

    /// <summary>
    /// Adds (or replaces, for the same date) a dated exchange rate. The rate
    /// must be positive; when the currency is the company base currency
    /// (<paramref name="baseCurrencyCode"/>), the rate is definitionally 1.
    /// </summary>
    public async Task<Guid> AddRateAsync(string companyId, CurrencyRate rate, string userId, string? baseCurrencyCode = null, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(rate);
        rate.CurrencyCode = NormalizeCurrencyCode(rate.CurrencyCode, "Currency");
        if (rate.Rate <= 0m)
            throw new InvalidOperationException("Exchange rate must be greater than zero.");
        if (!string.IsNullOrWhiteSpace(baseCurrencyCode)
            && string.Equals(rate.CurrencyCode, baseCurrencyCode.Trim(), StringComparison.OrdinalIgnoreCase)
            && rate.Rate != 1m)
            throw new InvalidOperationException("The exchange rate of the base currency is always 1.");
        if (rate.MinRate.HasValue && rate.MinRate.Value <= 0m)
            throw new InvalidOperationException("Minimum rate must be greater than zero when supplied.");
        if (rate.MaxRate.HasValue && rate.MaxRate.Value <= 0m)
            throw new InvalidOperationException("Maximum rate must be greater than zero when supplied.");
        if (rate.MinRate.HasValue && rate.MaxRate.HasValue && rate.MaxRate.Value < rate.MinRate.Value)
            throw new InvalidOperationException("Maximum rate cannot be below the minimum rate.");
        if (rate.MinRate.HasValue && rate.Rate < rate.MinRate.Value)
            throw new InvalidOperationException("Rate is below the allowed minimum for this currency.");
        if (rate.MaxRate.HasValue && rate.Rate > rate.MaxRate.Value)
            throw new InvalidOperationException("Rate is above the allowed maximum for this currency.");
        await _repository.SaveRateAsync(companyId, rate, RequireUser(userId), cancellationToken).ConfigureAwait(false);
        return rate.Id;
    }

    // ----------------------------------------------------------------- Parties

    public Task<IReadOnlyList<Party>> GetPartiesAsync(string companyId, string? kind = null, CancellationToken cancellationToken = default)
        => _repository.GetPartiesAsync(RequireCompany(companyId), NormalizeKindOrNull(kind), cancellationToken);

    public Task<Party?> GetPartyByCodeAsync(string companyId, string kind, string code, CancellationToken cancellationToken = default)
        => _repository.GetPartyByCodeAsync(RequireCompany(companyId), NormalizeKind(kind), RequireCode(code, "Party"), cancellationToken);

    public Task<IReadOnlyList<Party>> SearchPartiesAsync(string companyId, string? kind, string query, CancellationToken cancellationToken = default)
        => _repository.SearchPartiesAsync(RequireCompany(companyId), NormalizeKindOrNull(kind), query ?? string.Empty, cancellationToken);

    public async Task<Guid> SavePartyAsync(string companyId, Party party, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(party);
        party.Kind = NormalizeKind(party.Kind);
        RequireCode(party.Code, "Party");
        RequireName(party.NameEn, "Party");
        if (party.CreditLimit < 0m)
            throw new InvalidOperationException("Credit limit cannot be negative.");
        if (party.CreditDays < 0 || party.CreditDays > 3650)
            throw new InvalidOperationException("Credit days must be between 0 and 3650.");
        if (!string.IsNullOrWhiteSpace(party.Email) && !IsPlausibleEmail(party.Email))
            throw new InvalidOperationException("Email address is not valid.");
        party.Code = party.Code.Trim();
        party.NameEn = party.NameEn.Trim();
        await _repository.SavePartyAsync(companyId, party, RequireUser(userId), cancellationToken).ConfigureAwait(false);
        return party.Id;
    }

    public async Task DeletePartyAsync(string companyId, Guid id, string kind, string code, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (id == Guid.Empty)
            throw new ArgumentException("Party is required.", nameof(id));
        var refs = await _repository.CountPartyLedgerRefsAsync(companyId, NormalizeKind(kind), RequireCode(code, "Party"), cancellationToken).ConfigureAwait(false);
        if (refs > 0)
            throw new InvalidOperationException($"Party '{code.Trim()}' cannot be deleted because {refs} party ledger entr{(refs == 1 ? "y references" : "ies reference")} it. Deactivate it instead.");
        await _repository.SoftDeletePartyAsync(companyId, id, RequireUser(userId), cancellationToken).ConfigureAwait(false);
    }

    public Task<LedgerBalance> GetPartyBalanceAsync(string companyId, string kind, string code, CancellationToken cancellationToken = default)
        => _repository.GetPartyBalanceAsync(RequireCompany(companyId), NormalizeKind(kind), RequireCode(code, "Party"), cancellationToken);

    // ------------------------------------------------------------ Party groups

    public Task<IReadOnlyList<PartyGroup>> GetPartyGroupsAsync(string companyId, string? kind = null, CancellationToken cancellationToken = default)
        => _repository.GetPartyGroupsAsync(RequireCompany(companyId), NormalizeKindOrNull(kind), cancellationToken);

    public async Task<Guid> SavePartyGroupAsync(string companyId, PartyGroup group, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(group);
        group.Kind = NormalizeKind(group.Kind);
        RequireCode(group.Code, "Party group");
        RequireName(group.NameEn, "Party group");
        if (group.ParentId.HasValue && group.ParentId.Value == group.Id)
            throw new InvalidOperationException("A party group cannot be its own parent.");
        group.Code = group.Code.Trim();
        group.NameEn = group.NameEn.Trim();
        await _repository.SavePartyGroupAsync(companyId, group, RequireUser(userId), cancellationToken).ConfigureAwait(false);
        return group.Id;
    }

    public Task DeletePartyGroupAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Party group is required.", nameof(id));
        return _repository.SoftDeletePartyGroupAsync(RequireCompany(companyId), id, RequireUser(userId), cancellationToken);
    }

    // -------------------------------------------------------------- Sales reps

    public Task<IReadOnlyList<SalesRep>> GetSalesRepsAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetSalesRepsAsync(RequireCompany(companyId), cancellationToken);

    public Task<SalesRep?> GetSalesRepByCodeAsync(string companyId, string code, CancellationToken cancellationToken = default)
        => _repository.GetSalesRepByCodeAsync(RequireCompany(companyId), RequireCode(code, "Sales rep"), cancellationToken);

    public async Task<Guid> SaveSalesRepAsync(string companyId, SalesRep rep, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(rep);
        RequireCode(rep.Code, "Sales rep");
        RequireName(rep.NameEn, "Sales rep");
        if (rep.CommissionPct < 0m || rep.CommissionPct > 100m)
            throw new InvalidOperationException("Commission percentage must be between 0 and 100.");
        if (rep.TargetAmount < 0m)
            throw new InvalidOperationException("Target amount cannot be negative.");
        rep.Code = rep.Code.Trim();
        rep.NameEn = rep.NameEn.Trim();
        await _repository.SaveSalesRepAsync(companyId, rep, RequireUser(userId), cancellationToken).ConfigureAwait(false);
        return rep.Id;
    }

    public Task DeleteSalesRepAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Sales rep is required.", nameof(id));
        return _repository.SoftDeleteSalesRepAsync(RequireCompany(companyId), id, RequireUser(userId), cancellationToken);
    }

    // -------------------------------------------------------- Sales rep groups

    public Task<IReadOnlyList<SalesRepGroup>> GetSalesRepGroupsAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetSalesRepGroupsAsync(RequireCompany(companyId), cancellationToken);

    public async Task<Guid> SaveSalesRepGroupAsync(string companyId, SalesRepGroup group, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(group);
        RequireCode(group.Code, "Sales rep group");
        RequireName(group.NameEn, "Sales rep group");
        group.Code = group.Code.Trim();
        group.NameEn = group.NameEn.Trim();
        await _repository.SaveSalesRepGroupAsync(companyId, group, RequireUser(userId), cancellationToken).ConfigureAwait(false);
        return group.Id;
    }

    public Task DeleteSalesRepGroupAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Sales rep group is required.", nameof(id));
        return _repository.SoftDeleteSalesRepGroupAsync(RequireCompany(companyId), id, RequireUser(userId), cancellationToken);
    }

    // ----------------------------------------------------------------- Helpers

    private static string RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
        return companyId;
    }

    private static string RequireUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException("A current user is required for audited writes.");
        return userId;
    }

    private static string RequireCode(string? code, string entity)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException($"{entity} code is required.");
        return code.Trim();
    }

    private static void RequireName(string? name, string entity)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException($"{entity} name (English) is required.");
    }

    private static string NormalizeKind(string? kind)
    {
        var normalized = (kind ?? string.Empty).Trim().ToUpperInvariant();
        if (!PartyKinds.IsValid(normalized))
            throw new ArgumentException("Party kind must be 'C' (customer) or 'S' (supplier).", nameof(kind));
        return normalized;
    }

    private static string? NormalizeKindOrNull(string? kind)
        => string.IsNullOrWhiteSpace(kind) ? null : NormalizeKind(kind);

    private static string NormalizeCurrencyCode(string? code, string entity)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length != 3 || !normalized.All(char.IsLetter))
            throw new InvalidOperationException($"{entity} code must be exactly 3 letters.");
        return normalized;
    }

    private static bool IsPlausibleEmail(string email)
    {
        var trimmed = email.Trim();
        var at = trimmed.IndexOf('@');
        return at > 0 && trimmed.LastIndexOf('.') > at + 1 && !trimmed.Contains(' ');
    }
}
