// shoutech_erp_v0.1.6/src/ShouTech.Domain/Entities/RolPerm.cs
using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Entities;

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