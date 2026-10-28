// shoutech_erp_v0.1.6/src/ShouTech.Application/Services/LicSvc.cs
using ShouTech.Domain.Enums;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>
/// Central commercial license gate.
/// Every write operation and every optional module must pass through this service.
/// </summary>
public sealed class LicSvc
{
    private readonly ILicProv _provider;

    public LicSvc(ILicProv provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public async Task EnsureUsableAsync(CancellationToken ct = default)
    {
        var r = await _provider.ValidateAsync(ct);
        if (!r.IsUsable)
            throw new UnauthorizedAccessException(r.Message ?? "License is not valid.");
    }

    /// <summary>
    /// Blocks any create / edit / post when license is Viewer or unusable.
    /// </summary>
    public async Task EnsureCanWriteAsync(CancellationToken ct = default)
    {
        var r = await _provider.ValidateAsync(ct);

        if (!r.IsUsable)
            throw new UnauthorizedAccessException(r.Message ?? "License is not valid.");

        if (r.IsViewer || !_provider.CanWrite)
            throw new UnauthorizedAccessException("Read-only license (Viewer). Writing is not allowed.");
    }

    /// <summary>
    /// Ensures the requested module is present in the license.
    /// Core modules are automatically allowed for Core/Demo.
    /// </summary>
    public async Task EnsureModuleAsync(string moduleKey, CancellationToken ct = default)
    {
        var r = await _provider.ValidateAsync(ct);

        if (!r.IsUsable)
            throw new UnauthorizedAccessException(r.Message ?? "License is not valid.");

        if (!_provider.IsModuleEnabled(moduleKey))
            throw new UnauthorizedAccessException($"Module '{moduleKey}' is not licensed.");
    }

    public bool IsModuleEnabled(string moduleKey) => _provider.IsModuleEnabled(moduleKey);

    public async Task<LicResult> GetStatusAsync(CancellationToken ct = default)
        => await _provider.ValidateAsync(ct);
}