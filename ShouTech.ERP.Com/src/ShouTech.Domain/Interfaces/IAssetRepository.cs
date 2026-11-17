using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

public interface IAssetRepository
{
    Task<IReadOnlyList<FixedAsset>> GetAllAsync(string companyId, CancellationToken cancellationToken = default);
    Task SaveAsync(string companyId, FixedAsset asset, string userId, CancellationToken cancellationToken = default);
    Task SoftDeleteAsync(string companyId, Guid assetId, string userId, CancellationToken cancellationToken = default);
}
