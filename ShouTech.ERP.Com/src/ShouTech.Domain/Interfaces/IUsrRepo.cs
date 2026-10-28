// shoutech_erp_v0.1.6/src/ShouTech.Application/Interfaces/IUsrRepo.cs
using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

public interface IUsrRepo
{
    Task<Usr?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Usr?> GetByUserNameAsync(string userName, CancellationToken ct = default);
    Task AddAsync(Usr user, CancellationToken ct = default);
    Task UpdateAsync(Usr user, CancellationToken ct = default);
}
