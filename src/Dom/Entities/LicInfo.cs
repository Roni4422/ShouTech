// shoutech_erp_v0.1.6/src/ShouTech.Domain/Entities/LicInfo.cs
using ShouTech.Domain.Entities;

using ShouTech.Domain.Enums;

namespace ShouTech.Domain.Entities;

/// <summary>
/// Stores the current license state in the Master database.
/// Actual cryptographic validation is performed by the native engine.
/// </summary>
public class LicInfo : EntBase
{
    public string LicenseKey { get; private set; } = string.Empty;
    public LicEd Edition { get; private set; }
    public LicStat Status { get; private set; }
    public DateTime? IssuedAt { get; private set; }
    public DateTime? ExpiresAt { get; private set; }
    public DateTime? GracePeriodEndsAt { get; private set; }
    public int MaxDevices { get; private set; }
    public string? CustomerName { get; private set; }
    public string? CustomerEmail { get; private set; }
    public string? Notes { get; private set; }

    private LicInfo() { }

    public static LicInfo Create(
        string licenseKey,
        LicEd edition,
        DateTime? expiresAt,
        int maxDevices,
        Guid createdBy,
        string? customerName = null,
        string? customerEmail = null)
    {
        if (string.IsNullOrWhiteSpace(licenseKey))
            throw new ArgumentException("License key is required.", nameof(licenseKey));

        return new LicInfo
        {
            Id = Guid.NewGuid(),
            LicenseKey = licenseKey.Trim(),
            Edition = edition,
            Status = LicStat.Valid,
            IssuedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt,
            MaxDevices = maxDevices,
            CustomerName = customerName?.Trim(),
            CustomerEmail = customerEmail?.Trim(),
            CreatedAt = DateTime.UtcNow,
            CreatedBy = createdBy
        };
    }

    public void UpdateStatus(LicStat status, DateTime? gracePeriodEndsAt, Guid modifiedBy)
    {
        Status = status;
        GracePeriodEndsAt = gracePeriodEndsAt;
        Touch(modifiedBy);
    }
}