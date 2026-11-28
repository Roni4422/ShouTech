namespace ShouTech.Domain.Entities;

/// <summary>
/// A versioned bill of materials (migration 058): the reusable recipe for
/// one finished material. Exactly one version per material is normally
/// flagged <see cref="Bom.IsDefault"/>; several versions may be active.
/// A component line may itself have a BOM — explosion expands multi-level
/// and refuses cycles with MFG-1001.
/// </summary>
public sealed class Bom
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string MaterialCode { get; set; } = string.Empty;
    public string? MaterialName { get; set; }
    /// <summary>Human-readable manufacturing template label used by the template picker.</summary>
    public string PickerLabel => string.IsNullOrWhiteSpace(MaterialName)
        ? $"{MaterialCode} · v{Version}"
        : $"{MaterialName} ({MaterialCode}) · v{Version}";
    /// <summary>Version number within (company, material); starts at 1.</summary>
    public int Version { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public bool IsDefault { get; set; }
    /// <summary>Batch size: the quantity of finished material this BOM
    /// produces; line quantities are stated per this output quantity.</summary>
    public decimal OutputQty { get; set; } = 1m;
    public string OutputUnit { get; set; } = "PCS";
    public string InputDocumentKind { get; set; } = "MATERIAL_ISSUE";
    public string OutputDocumentKind { get; set; } = "MATERIAL_RECEIPT";
    public string? InputWarehouseCode { get; set; }
    public string? OutputWarehouseCode { get; set; }
    public string? Notes { get; set; }
    public List<BomLine> Lines { get; set; } = new();
    public List<BomExpense> Expenses { get; set; } = new();
    public List<BomOutput> Outputs { get; set; } = new();

    /// <summary>Rolled standard cost of one output batch at the lines'
    /// snapshot standard costs (qty x standard unit cost, scrap excluded —
    /// scrap is a quantity allowance, not a costed input).</summary>
    public decimal StandardBatchCost =>
        Lines.Sum(l => decimal.Round(l.Qty * l.StandardUnitCost, 2)) +
        Expenses.Sum(e => decimal.Round(e.Amount, 2));

    /// <summary>Standard cost allocated to the primary output. Secondary
    /// outputs take their declared share; any unallocated balance remains
    /// with the primary output.</summary>
    public decimal StandardUnitCost
    {
        get
        {
            if (OutputQty <= 0) return 0m;
            var main = Outputs.FirstOrDefault(o => string.Equals(o.OutputKind, "MAIN", StringComparison.OrdinalIgnoreCase));
            var secondaryShare = Outputs.Where(o => !string.Equals(o.OutputKind, "MAIN", StringComparison.OrdinalIgnoreCase))
                .Sum(o => o.CostSharePct);
            var mainShare = main?.CostSharePct > 0m ? main.CostSharePct : Math.Max(0m, 100m - secondaryShare);
            return decimal.Round(StandardBatchCost * mainShare / 100m / OutputQty, 4, MidpointRounding.AwayFromZero);
        }
    }
}

/// <summary>One component line of a BOM. Qty is expressed in the line's
/// own unit per <see cref="Bom.OutputQty"/> of finished output;
/// ScrapPct is the waste allowance explosion adds on top
/// (required = qty x (1 + scrap/100), ERPNext/Odoo pattern).</summary>
public sealed class BomLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BomId { get; set; }
    public int LineNo { get; set; }
    public string ComponentCode { get; set; } = string.Empty;
    public string? ComponentName { get; set; }
    public decimal Qty { get; set; }
    public string? UnitCode { get; set; }
    public decimal ScrapPct { get; set; }
    /// <summary>Per-line source warehouse (code or id text, the same
    /// convention as mfg_orders.warehouse_code); null = order default.</summary>
    public string? WarehouseCode { get; set; }
    /// <summary>Standard cost snapshot per line unit, taken from the
    /// valuation engine's current rate when the BOM is saved.</summary>
    public decimal StandardUnitCost { get; set; }
}

/// <summary>One LEAF requirement produced by BOM explosion: a component
/// with no (active) BOM of its own, with the fully multiplied quantity.
/// Qty is in the leaf line's display unit; BaseQty is the same quantity
/// in the material's base (first) unit — the unit stock is kept in.</summary>
public sealed record BomRequirement(
    string MaterialCode,
    string? MaterialName,
    decimal Qty,
    string? UnitCode,
    decimal BaseQty,
    decimal StandardUnitCost,
    string? WarehouseCode,
    int Level)
{
    /// <summary>Standard value of the requirement at the snapshot rate
    /// (per display unit).</summary>
    public decimal StandardAmount => decimal.Round(Qty * StandardUnitCost, 2);
}

/// <summary>The result of exploding one BOM for a requested output
/// quantity: leaf requirements aggregated per material + warehouse, and
/// the rolled standard cost of the whole explosion.</summary>
public sealed record BomExplosion(
    Guid BomId,
    string MaterialCode,
    int BomVersion,
    decimal RequestedQty,
    IReadOnlyList<BomRequirement> Requirements)
{
    public decimal TotalStandardCost => Requirements.Sum(r => r.StandardAmount);
}

/// <summary>A material as the BOM / manufacturing pickers offer it:
/// code, display name, base (first) unit and current valuation rate.</summary>
public sealed record MfgMaterialOption(
    string Code, string? Name, string? BaseUnit, decimal CurrentCost, long MaterialId);

/// <summary>A warehouse as the manufacturing pickers offer it.</summary>
public sealed record MfgWarehouseOption(long WarehouseId, string Code, string? Name);

/// <summary>Planned production expense in the reusable template. Amount is per template batch.</summary>
public sealed class BomExpense
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BomId { get; set; }
    public int LineNo { get; set; }
    public string ExpenseCode { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal Amount { get; set; }
    public string? DebitAccountCode { get; set; }
    public string? CreditAccountCode { get; set; }
    public string? CostCenter { get; set; }
}

/// <summary>Additional or primary output of a manufacturing template.</summary>
public sealed class BomOutput
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BomId { get; set; }
    public int LineNo { get; set; }
    public string MaterialCode { get; set; } = string.Empty;
    public string? MaterialName { get; set; }
    public string OutputKind { get; set; } = "MAIN";
    public decimal Qty { get; set; } = 1m;
    public string? UnitCode { get; set; }
    public string? WarehouseCode { get; set; }
    public decimal CostSharePct { get; set; }
}
