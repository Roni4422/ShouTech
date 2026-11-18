using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

public interface IAssetRepository
{
    Task<IReadOnlyList<FixedAsset>> GetAllAsync(string companyId, CancellationToken cancellationToken = default);
    Task SaveAsync(string companyId, FixedAsset asset, string userId, CancellationToken cancellationToken = default);
    Task SoftDeleteAsync(string companyId, Guid assetId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Posts the not-yet-posted accumulated depreciation of an
    /// asset to the GL (Dr depreciation expense / Cr accumulated
    /// depreciation) in one transaction and returns the journal document
    /// number, or null when nothing remains to post (Step 3, AST-01).</summary>
    Task<string?> PostDepreciationAsync(string companyId, Guid assetId, decimal accumulatedDepreciation, DateOnly asOf, string userId, CancellationToken cancellationToken = default);
}
