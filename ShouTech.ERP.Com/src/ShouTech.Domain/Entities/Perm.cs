// shoutech_erp_v0.1.6/src/ShouTech.Domain/Entities/Perm.cs
using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Entities;

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