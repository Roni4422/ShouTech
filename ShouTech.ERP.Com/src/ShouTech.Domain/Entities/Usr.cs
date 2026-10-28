// shoutech_erp_v0.1.6/src/ShouTech.Domain/Entities/Usr.cs
using ShouTech.Domain.Entities;
namespace ShouTech.Domain.Entities;

public class Usr : EntBase
{
    public string UserName { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public string? Email { get; private set; }
    public string? Mobile { get; private set; }
    public bool IsActive { get; private set; } = true;
    public bool IsSystem { get; private set; }
    public string PreferredLanguage { get; private set; } = "ar";
    public string PreferredTheme { get; private set; } = "Light";
    public DateTime? LastLoginAt { get; private set; }

    private Usr() { }

    public static Usr Create(
        string userName,
        string displayName,
        Guid createdBy,
        string? email = null,
        string? mobile = null,
        string preferredLanguage = "ar",
        bool isSystem = false)
    {
        if (string.IsNullOrWhiteSpace(userName))
            throw new ArgumentException("User name is required.", nameof(userName));
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Display name is required.", nameof(displayName));

        return new Usr
        {
            Id = Guid.NewGuid(),
            UserName = userName.Trim(),
            DisplayName = displayName.Trim(),
            Email = email?.Trim(),
            Mobile = mobile?.Trim(),
            PreferredLanguage = string.IsNullOrWhiteSpace(preferredLanguage) ? "ar" : preferredLanguage.Trim().ToLowerInvariant(),
            IsSystem = isSystem,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = createdBy
        };
    }

    public void UpdateProfile(
        string displayName,
        string? email,
        string? mobile,
        string preferredLanguage,
        string preferredTheme,
        Guid modifiedBy)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Display name is required.", nameof(displayName));

        DisplayName = displayName.Trim();
        Email = email?.Trim();
        Mobile = mobile?.Trim();
        PreferredLanguage = string.IsNullOrWhiteSpace(preferredLanguage) ? PreferredLanguage : preferredLanguage.Trim().ToLowerInvariant();
        PreferredTheme = string.IsNullOrWhiteSpace(preferredTheme) ? PreferredTheme : preferredTheme.Trim();
        Touch(modifiedBy);
    }

    public void SetActive(bool isActive, Guid modifiedBy)
    {
        if (IsSystem && !isActive)
            throw new InvalidOperationException("System accounts cannot be deactivated.");
        if (IsActive == isActive) return;
        IsActive = isActive;
        Touch(modifiedBy);
    }

    public void RecordLogin() => LastLoginAt = DateTime.UtcNow;
}