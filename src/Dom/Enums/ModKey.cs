// shoutech_erp_v0.1.6/src/ShouTech.Domain/Enums/ModKey.cs

namespace ShouTech.Domain.Enums;

// ---- merged from src/Domain/Enums/AppMode.cs ----
    public enum AppMode
    {
        Standalone,
        Server,
        Client
    }

// ---- merged from src/Domain/Enums/LicenseType.cs ----
    public enum LicenseType
    {
        SingleDevice,
        NetworkServer,
        NetworkClient
    }

// ---- merged from src/Domain/Enums/LicStat.cs ----
public enum LicStat : byte
{
    Valid = 0,
    GracePeriod = 1,
    Expired = 2,
    Revoked = 3,
    HardwareMismatch = 4,
    InvalidSignature = 5,
    DeviceLimitExceeded = 6,
    NotFound = 7
}

// ---- merged from src/Domain/Enums/LicEd.cs ----
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

/// <summary>
/// Strongly-typed module keys used for licensing and dynamic UI.
/// Values must remain stable after commercial release.
/// </summary>
public static class ModKey
{
    // ===== Core (always present in paid Core license) =====
    public const string Acc = "ACC";          // Financial accounting
    public const string Inv = "INV";          // Inventory
    public const string Sales = "SALES";      // Core sales
    public const string Purch = "PURCH";      // Core purchasing

    // ===== Optional paid modules =====
    public const string Crm = "CRM";          // Customer relationship management (Phase B 057; entitlement gating postponed by the owner - the key is registered, no gate is enforced yet)
    public const string Hr = "HR";            // Human resources
    public const string Mfg = "MFG";          // Manufacturing
    public const string Pos = "POS";          // Point of sale
    public const string Ai = "AI";            // Artificial intelligence
    public const string AdvRpt = "ADV_RPT";   // Advanced reports
    public const string MultiCur = "MULTI_CUR"; // Advanced multi-currency
    public const string WhiteLabel = "WL";    // White-label branding
}
