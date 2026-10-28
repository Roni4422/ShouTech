// shoutech_erp_v0.1.6/src/ShouTech.Domain/Interfaces/ILicProv.cs
using ShouTech.Domain.Enums;

namespace ShouTech.Domain.Interfaces;

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