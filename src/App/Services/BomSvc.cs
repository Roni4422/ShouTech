using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>
/// BOM master service (migration 058): validation lives here; the
/// repository owns transactions, the standard-cost snapshot and the
/// cycle-safe explosion. A BOM is the reusable recipe the ribbon's
/// "Manufacturing templates" entry always promised (sweep MFG-6).
/// </summary>
public sealed class BomService
{
    private readonly IBomRepository _repository;
    public BomService(IBomRepository repository)
        => _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    internal IBomRepository Repository => _repository;

    public Task<IReadOnlyList<Bom>> GetAllAsync(string companyId, bool includeInactive = false, CancellationToken cancellationToken = default)
        => _repository.GetAllAsync(RequireCompany(companyId), includeInactive, cancellationToken);

    public Task<Bom?> GetByIdAsync(string companyId, Guid bomId, CancellationToken cancellationToken = default)
    {
        if (bomId == Guid.Empty) throw new ArgumentException("BOM is required.", nameof(bomId));
        return _repository.GetByIdAsync(RequireCompany(companyId), bomId, cancellationToken);
    }

    public Task<Bom?> GetDefaultForMaterialAsync(string companyId, string materialCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(materialCode)) throw new ArgumentException("Material is required.", nameof(materialCode));
        return _repository.GetDefaultForMaterialAsync(RequireCompany(companyId), materialCode.Trim(), cancellationToken);
    }

    public async Task<Guid> SaveAsync(string companyId, Bom bom, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        Validate(bom);
        await _repository.SaveAsync(companyId, bom, RequireUser(userId), cancellationToken).ConfigureAwait(false);
        return bom.Id;
    }

    public Task DeleteAsync(string companyId, Guid bomId, string userId, CancellationToken cancellationToken = default)
    {
        if (bomId == Guid.Empty) throw new ArgumentException("BOM is required.", nameof(bomId));
        return _repository.SoftDeleteAsync(RequireCompany(companyId), bomId, RequireUser(userId), cancellationToken);
    }

    public Task<BomExplosion> ExplodeAsync(string companyId, Guid bomId, decimal outputQty, CancellationToken cancellationToken = default)
    {
        if (bomId == Guid.Empty) throw new ArgumentException("BOM is required.", nameof(bomId));
        if (outputQty <= 0) throw new InvalidOperationException("The output quantity must be greater than zero.");
        return _repository.ExplodeAsync(RequireCompany(companyId), bomId, outputQty, cancellationToken);
    }

    public Task<BomExplosion> ExplodeForMaterialAsync(string companyId, string materialCode, decimal outputQty, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(materialCode)) throw new ArgumentException("Material is required.", nameof(materialCode));
        if (outputQty <= 0) throw new InvalidOperationException("The output quantity must be greater than zero.");
        return _repository.ExplodeForMaterialAsync(RequireCompany(companyId), materialCode.Trim(), outputQty, cancellationToken);
    }

    public Task<IReadOnlyList<MfgMaterialOption>> GetMaterialOptionsAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetMaterialOptionsAsync(RequireCompany(companyId), cancellationToken);

    public Task<IReadOnlyList<MfgWarehouseOption>> GetWarehouseOptionsAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetWarehouseOptionsAsync(RequireCompany(companyId), cancellationToken);

    private static void Validate(Bom bom)
    {
        ArgumentNullException.ThrowIfNull(bom);
        if (string.IsNullOrWhiteSpace(bom.MaterialCode))
            throw new InvalidOperationException("The finished material code is required.");
        if (bom.Version < 1)
            throw new InvalidOperationException("The BOM version must be at least 1.");
        if (bom.OutputQty <= 0)
            throw new InvalidOperationException("The BOM output quantity must be greater than zero.");
        if (bom.Lines is null || bom.Lines.Count == 0)
            throw new InvalidOperationException("A bill of materials requires at least one component line.");
        // Repeated component codes are valid. Manufacturing recipes may
        // intentionally consume the same material on separate operations,
        // from different warehouses, or with different issue quantities.
        // Keep each row distinct in the template; stock posting performs
        // the appropriate quantity aggregation at its own boundary.
        foreach (var line in bom.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.ComponentCode))
                throw new InvalidOperationException("Every BOM line requires a component code.");
            if (line.Qty <= 0)
                throw new InvalidOperationException($"Line quantity for '{line.ComponentCode}' must be greater than zero.");
            if (line.ScrapPct is < 0 or >= 100)
                throw new InvalidOperationException($"Scrap percentage for '{line.ComponentCode}' must be between 0 and 100.");
            line.ComponentCode = line.ComponentCode.Trim();
        }
        if (bom.Outputs is null) bom.Outputs = new List<BomOutput>();
        if (bom.Expenses is null) bom.Expenses = new List<BomExpense>();
        if (bom.Outputs.Count == 0)
            bom.Outputs.Add(new BomOutput { BomId = bom.Id, MaterialCode = bom.MaterialCode.Trim(), MaterialName = bom.MaterialName, OutputKind = "MAIN", Qty = bom.OutputQty, UnitCode = bom.OutputUnit });
        if (bom.Outputs.Count(o => string.Equals(o.OutputKind, "MAIN", StringComparison.OrdinalIgnoreCase)) != 1)
            throw new InvalidOperationException("A manufacturing template must contain exactly one MAIN output.");
        var mainOutput = bom.Outputs.Single(o => string.Equals(o.OutputKind, "MAIN", StringComparison.OrdinalIgnoreCase));
        if (!string.Equals(mainOutput.MaterialCode.Trim(), bom.MaterialCode.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The MAIN output must match the finished material in the template header.");
        if (mainOutput.Qty != bom.OutputQty || !string.Equals(mainOutput.UnitCode, bom.OutputUnit, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The MAIN output quantity and unit must match the template batch quantity and unit.");
        if (bom.Outputs.Any(o => string.IsNullOrWhiteSpace(o.MaterialCode) || o.Qty <= 0))
            throw new InvalidOperationException("Every output requires a material code and a positive quantity.");
        if (bom.Outputs.Any(o => o.CostSharePct is < 0 or > 100) || bom.Outputs.Sum(o => o.CostSharePct) > 100m)
            throw new InvalidOperationException("Output cost-share percentages must total no more than 100.");
        if (bom.Expenses.Any(e => string.IsNullOrWhiteSpace(e.ExpenseCode) || e.Amount < 0))
            throw new InvalidOperationException("Each expense requires a code and a non-negative amount.");
        bom.MaterialCode = bom.MaterialCode.Trim();
        if (string.IsNullOrWhiteSpace(bom.OutputUnit)) bom.OutputUnit = "PCS";
    }

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
}
