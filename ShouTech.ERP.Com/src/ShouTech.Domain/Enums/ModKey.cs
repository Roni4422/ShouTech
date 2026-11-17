// shoutech_erp_v0.1.6/src/ShouTech.Domain/Enums/ModKey.cs
namespace ShouTech.Domain.Enums;

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
    public const string Hr = "HR";            // Human resources
    public const string Mfg = "MFG";          // Manufacturing
    public const string Pos = "POS";          // Point of sale
    public const string Ai = "AI";            // Artificial intelligence
    public const string AdvRpt = "ADV_RPT";   // Advanced reports
    public const string MultiCur = "MULTI_CUR"; // Advanced multi-currency
    public const string WhiteLabel = "WL";    // White-label branding
}