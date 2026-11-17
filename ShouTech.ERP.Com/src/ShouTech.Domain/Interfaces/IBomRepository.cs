using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

/// <summary>
/// BOM master persistence + explosion (migration 058). Company id is the
/// company CODE (the manufacturing / materials convention). Explosion is
/// multi-level and cycle-safe: a BOM that references its own finished
/// material directly or transitively is refused with MFG-1001, both at
/// save time and at explosion time (a cycle can also be created by
/// activating a second BOM after the first was saved).
/// </summary>
public interface IBomRepository
{
    Task<IReadOnlyList<Bom>> GetAllAsync(string companyId, bool includeInactive = false, CancellationToken cancellationToken = default);

    Task<Bom?> GetByIdAsync(string companyId, Guid bomId, CancellationToken cancellationToken = default);

    /// <summary>The version a new order should use for a material: the
    /// default active version, else the highest active version.</summary>
    Task<Bom?> GetDefaultForMaterialAsync(string companyId, string materialCode, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces a BOM (header + lines) in one
    /// transaction. Line standard costs are re-snapshotted from the
    /// valuation engine's current rate for each component. Setting
    /// IsDefault clears the default flag on the material's other
    /// versions. A direct self-reference is refused with MFG-1001.</summary>
    Task SaveAsync(string companyId, Bom bom, string userId, CancellationToken cancellationToken = default);

    Task SoftDeleteAsync(string companyId, Guid bomId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Expands a BOM for a requested output quantity (in the
    /// BOM's output unit) into aggregated LEAF requirements: quantities
    /// carry scrap allowances and unit-equivalent conversion to base
    /// units. Throws MFG-1001 on a cycle, MFG-1002 when the BOM is
    /// missing or inactive.</summary>
    Task<BomExplosion> ExplodeAsync(string companyId, Guid bomId, decimal outputQty, CancellationToken cancellationToken = default);

    /// <summary>Explodes the default BOM of a material; same contract as
    /// <see cref="ExplodeAsync"/>.</summary>
    Task<BomExplosion> ExplodeForMaterialAsync(string companyId, string materialCode, decimal outputQty, CancellationToken cancellationToken = default);

    /// <summary>Materials of the company for pickers (code, name, base
    /// unit, current valuation rate from the engine balances).</summary>
    Task<IReadOnlyList<MfgMaterialOption>> GetMaterialOptionsAsync(string companyId, CancellationToken cancellationToken = default);

    /// <summary>Warehouses of the company for pickers.</summary>
    Task<IReadOnlyList<MfgWarehouseOption>> GetWarehouseOptionsAsync(string companyId, CancellationToken cancellationToken = default);
}
