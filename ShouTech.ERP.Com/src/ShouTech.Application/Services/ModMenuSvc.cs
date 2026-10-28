// shoutech_erp_v0.1.6/src/ShouTech.Application/Services/ModMenuSvc.cs
using ShouTech.Domain.Enums;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>
/// Builds the list of modules that should appear in the main ribbon/menu
/// according to the current license (exactly as local market leaders do).
/// </summary>
public sealed class ModMenuSvc
{
    private readonly ILicProv _lic;

    public ModMenuSvc(ILicProv lic)
    {
        _lic = lic ?? throw new ArgumentNullException(nameof(lic));
    }

    /// <summary>
    /// Returns the fastest modern launch sequence for day-to-day ERP work.
    /// This keeps the UI simpler and faster than legacy ERP products.
    /// </summary>
    public IReadOnlyList<string> GetModernLaunchSequence()
    {
        return new[]
        {
            ModKey.Acc,
            ModKey.Inv,
            ModKey.Sales,
            ModKey.Purch,
            ModKey.AdvRpt,
            ModKey.Pos,
            ModKey.Ai
        };
    }

    /// <summary>
    /// Returns ordered list of module keys that must be shown in the main UI.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetVisibleModulesAsync(CancellationToken ct = default)
    {
        var result = await _lic.ValidateAsync(ct);
        var list = new List<string>();

        // Demo: show core + optional for trial experience
        if (result.IsDemo)
        {
            list.AddRange(new[] { ModKey.Acc, ModKey.Inv, ModKey.Sales, ModKey.Purch });
            // optionally add more in demo if desired
            return list;
        }

        // Viewer or Core: always show core modules when license is usable
        if (result.IsUsable)
        {
            list.Add(ModKey.Acc);
            list.Add(ModKey.Inv);
            list.Add(ModKey.Sales);
            list.Add(ModKey.Purch);
        }

        // Optional modules – only if explicitly licensed
        void AddIf(string key)
        {
            if (_lic.IsModuleEnabled(key))
                list.Add(key);
        }

        AddIf(ModKey.Hr);
        AddIf(ModKey.Mfg);
        AddIf(ModKey.Pos);
        AddIf(ModKey.Ai);
        AddIf(ModKey.AdvRpt);
        AddIf(ModKey.MultiCur);
        AddIf(ModKey.WhiteLabel);

        return list;
    }
}