// shoutech_erp_v0.1.6/src/ShouTech.Domain/Entities/Device.cs
using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Entities;

/// <summary>
/// Registered device bound to a license (HWID + optional dongle info).
/// </summary>
public class Device : EntBase
{
    public Guid LicenseId { get; private set; }
    public string HardwareId { get; private set; } = string.Empty;
    public string? DongleSerial { get; private set; }
    public string? MachineName { get; private set; }
    public string? OsVersion { get; private set; }
    public DateTime? LastSeenAt { get; private set; }
    public bool IsActive { get; private set; } = true;

    private Device() { }

    public static Device Create(
        Guid licenseId,
        string hardwareId,
        Guid createdBy,
        string? dongleSerial = null,
        string? machineName = null,
        string? osVersion = null)
    {
        if (licenseId == Guid.Empty)
            throw new ArgumentException("LicenseId is required.", nameof(licenseId));
        if (string.IsNullOrWhiteSpace(hardwareId))
            throw new ArgumentException("HardwareId is required.", nameof(hardwareId));

        return new Device
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            HardwareId = hardwareId.Trim(),
            DongleSerial = dongleSerial?.Trim(),
            MachineName = machineName?.Trim(),
            OsVersion = osVersion?.Trim(),
            IsActive = true,
            LastSeenAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = createdBy
        };
    }

    public void UpdateLastSeen()
    {
        LastSeenAt = DateTime.UtcNow;
    }

    public void SetActive(bool isActive, Guid modifiedBy)
    {
        if (IsActive == isActive) return;
        IsActive = isActive;
        Touch(modifiedBy);
    }
}