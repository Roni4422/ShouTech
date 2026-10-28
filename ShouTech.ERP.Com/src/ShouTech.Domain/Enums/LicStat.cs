// shoutech_erp_v0.1.6/src/ShouTech.Domain/Enums/LicStat.cs
namespace ShouTech.Domain.Enums;

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