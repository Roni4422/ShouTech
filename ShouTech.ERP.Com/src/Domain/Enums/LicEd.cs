// shoutech_erp_v0.1.6/src/ShouTech.Domain/Enums/LicEd.cs
namespace ShouTech.Domain.Enums;

/// <summary>
/// Commercial license state (not feature set).
/// Feature set is controlled by Module Flags.
/// </summary>
public enum LicEd : byte
{
    /// <summary>Time-limited full or partial trial.</summary>
    Demo = 0,

    /// <summary>Read-only. No create / edit / post allowed.</summary>
    Viewer = 1,

    /// <summary>Paid core license (Accounting + Inventory + basic Sales/Purchase).</summary>
    Core = 10
}