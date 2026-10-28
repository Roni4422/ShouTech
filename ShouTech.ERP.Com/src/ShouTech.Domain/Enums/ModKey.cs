// shoutech_erp_v0.1.6/src/ShouTech.Domain/Enums/ModKey.cs
namespace ShouTech.Domain.Enums;

/// <summary>
/// Strongly-typed module keys used for licensing and dynamic UI.
/// Values must remain stable after commercial release.
/// </summary>
public static class ModKey
{
    // ===== Core (always present in paid Core license) =====
    public const string Acc = "ACC";          // المحاسبة المالية
    public const string Inv = "INV";          // المستودعات
    public const string Sales = "SALES";      // المبيعات الأساسية
    public const string Purch = "PURCH";      // المشتريات الأساسية

    // ===== Optional paid modules =====
    public const string Hr = "HR";            // الموارد البشرية
    public const string Mfg = "MFG";          // التصنيع
    public const string Pos = "POS";          // نقطة البيع
    public const string Ai = "AI";            // الذكاء الاصطناعي
    public const string AdvRpt = "ADV_RPT";   // تقارير متقدمة
    public const string MultiCur = "MULTI_CUR"; // عملات متعددة متقدمة
    public const string WhiteLabel = "WL";    // تخصيص الهوية
}