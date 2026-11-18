// shoutech_erp_v0.1.6/src/ShouTech.Domain/Entities/Rol.cs
using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Entities;

public class Rol : EntBase
{
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public bool IsSystem { get; private set; }
    public bool IsActive { get; private set; } = true;

    private Rol() { }

    public static Rol Create(
        string code,
        string name,
        Guid createdBy,
        string? description = null,
        bool isSystem = false)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Role code is required.", nameof(code));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Role name is required.", nameof(name));

        return new Rol
        {
            Id = Guid.NewGuid(),
            Code = code.Trim().ToUpperInvariant(),
            Name = name.Trim(),
            Description = description?.Trim(),
            IsSystem = isSystem,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = createdBy
        };
    }

    public void Update(string name, string? description, Guid modifiedBy)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Role name is required.", nameof(name));

        Name = name.Trim();
        Description = description?.Trim();
        Touch(modifiedBy);
    }

    public void SetActive(bool isActive, Guid modifiedBy)
    {
        if (IsSystem && !isActive)
            throw new InvalidOperationException("System roles cannot be deactivated.");
        if (IsActive == isActive) return;
        IsActive = isActive;
        Touch(modifiedBy);
    }
}