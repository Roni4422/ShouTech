// shoutech_erp_v0.1.6/src/ShouTech.Domain/Interfaces/IComRepo.cs
using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

public interface IComRepo1
{
    Task AddAsync(Comp company, CancellationToken cancellationToken = default);
    Task<bool> CodeExistsAsync(string code, Guid? excludeId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Comp>> GetAllAsync(bool activeOnly = true, CancellationToken cancellationToken = default);
    Task<Comp?> GetByCodeAsync(string code, bool includeDeleted = false, CancellationToken cancellationToken = default);
    Task<Comp?> GetByIdAsync(Guid id, bool includeDeleted = false, CancellationToken cancellationToken = default);
    Task SoftDeleteAsync(Guid id, Guid deletedBy, CancellationToken cancellationToken = default);
    Task UpdateAsync(Comp company, CancellationToken cancellationToken = default);
}

public interface IComRepo
{
    Task<Comp?> GetByIdAsync(Guid id, bool includeDeleted = false, CancellationToken cancellationToken = default);
    Task<Comp?> GetByCodeAsync(string code, bool includeDeleted = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Comp>> GetAllAsync(bool activeOnly = true, CancellationToken cancellationToken = default);
    Task AddAsync(Comp company, CancellationToken cancellationToken = default);
    Task UpdateAsync(Comp company, CancellationToken cancellationToken = default);
    Task SoftDeleteAsync(Guid id, Guid deletedBy, CancellationToken cancellationToken = default);
    Task<bool> CodeExistsAsync(string code, Guid? excludeId = null, CancellationToken cancellationToken = default);
}