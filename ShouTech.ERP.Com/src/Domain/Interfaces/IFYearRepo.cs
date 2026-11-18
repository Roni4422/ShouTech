// shoutech_erp_v0.1.6/src/ShouTech.Application/Interfaces/IFYearRepo.cs
using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

public interface IFYearRepo
{
    Task<FYear?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<FYear>> GetByCompanyAsync(Guid companyId, CancellationToken ct = default);
    Task AddAsync(FYear year, CancellationToken ct = default);
    Task UpdateAsync(FYear year, CancellationToken ct = default);
    Task ClearCurrentForCompanyAsync(Guid companyId, Guid modifiedBy, CancellationToken ct = default);
}
