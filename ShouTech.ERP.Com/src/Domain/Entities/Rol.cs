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

// ---- merged from src/Domain/Entities/RolPerm.cs ----
/// <summary>
/// Many-to-many link between Role and Permission.
/// </summary>
public class RolPerm : EntBase
{
    public Guid RoleId { get; private set; }
    public Guid PermissionId { get; private set; }

    private RolPerm() { }

    public static RolPerm Create(Guid roleId, Guid permissionId, Guid createdBy)
    {
        if (roleId == Guid.Empty)
            throw new ArgumentException("RoleId is required.", nameof(roleId));
        if (permissionId == Guid.Empty)
            throw new ArgumentException("PermissionId is required.", nameof(permissionId));

        return new RolPerm
        {
            Id = Guid.NewGuid(),
            RoleId = roleId,
            PermissionId = permissionId,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = createdBy
        };
    }
}

// ---- merged from src/Domain/Entities/Perm.cs ----
public class Perm : EntBase
{
    public string Code { get; private set; } = string.Empty;          // e.g. "INV.VIEW", "FIN.POST"
    public string Name { get; private set; } = string.Empty;
    public string Module { get; private set; } = string.Empty;         // INV, FIN, CRM, SYS...
    public string? Description { get; private set; }
    public bool IsSystem { get; private set; } = true;

    private Perm() { }

    public static Perm Create(
        string code,
        string name,
        string module,
        Guid createdBy,
        string? description = null)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Permission code is required.", nameof(code));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Permission name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(module))
            throw new ArgumentException("Module is required.", nameof(module));

        return new Perm
        {
            Id = Guid.NewGuid(),
            Code = code.Trim().ToUpperInvariant(),
            Name = name.Trim(),
            Module = module.Trim().ToUpperInvariant(),
            Description = description?.Trim(),
            IsSystem = true,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = createdBy
        };
    }
}
