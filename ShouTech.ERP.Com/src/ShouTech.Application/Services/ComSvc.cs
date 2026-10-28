// shoutech_erp_v0.1.6/src/ShouTech.Application/Services/ComSvc.cs
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
using ShouTech.Domain.Enums;

namespace ShouTech.Application.Services;

/// <summary>
/// Application service for Company management.
/// Enforces business rules, licensing limits, and audit.
/// </summary>
public sealed class ComSvc
{
    private readonly IComRepo _repo;
    private readonly ILicProv _lic;

    public ComSvc(IComRepo repo, ILicProv lic)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _lic = lic ?? throw new ArgumentNullException(nameof(lic));
    }

    public async Task<Comp> CreateAsync(
        string code,
        string name,
        string nameSecondary,
        string currency,
        Guid userId,
        string? taxNumber = null,
        Guid? groupId = null,
        CancellationToken ct = default)
    {
        // Edition limit check (Starter = 1 company, Professional/Enterprise = more)
        var lic = await _lic.ValidateAsync(ct);
        if (!lic.IsUsable)
            throw new InvalidOperationException("License is not usable.");

        if (lic.Edition == LicEd.Core)
        {
            var existing = await _repo.GetAllAsync(activeOnly: false, ct);
            if (existing.Count >= 1)
                throw new InvalidOperationException("Starter edition allows only one company.");
        }

        if (await _repo.CodeExistsAsync(code, null, ct))
            throw new InvalidOperationException($"Company code '{code}' already exists.");

        var company = Comp.Create(code, name, nameSecondary, currency, userId, taxNumber, groupId);
        await _repo.AddAsync(company, ct);
        return company;
    }

    public async Task UpdateAsync(
        Guid id,
        string name,
        string nameSecondary,
        string? taxNumber,
        string currency,
        Guid? groupId,
        int sortOrder,
        string? notes,
        Guid userId,
        CancellationToken ct = default)
    {
        var company = await _repo.GetByIdAsync(id, false, ct)
            ?? throw new InvalidOperationException("Company not found.");

        company.Update(name, nameSecondary, taxNumber, currency, groupId, sortOrder, notes, userId);
        await _repo.UpdateAsync(company, ct);
    }

    public async Task SoftDeleteAsync(Guid id, Guid userId, CancellationToken ct = default)
    {
        var company = await _repo.GetByIdAsync(id, false, ct)
            ?? throw new InvalidOperationException("Company not found.");

        // Prevent deleting the last active company in lower editions if needed
        await _repo.SoftDeleteAsync(id, userId, ct);
    }

    public async Task<IReadOnlyList<Comp>> GetAllAsync(bool activeOnly = true, CancellationToken ct = default)
    {
        return await _repo.GetAllAsync(activeOnly, ct);
    }

    public async Task<Comp?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _repo.GetByIdAsync(id, false, ct);
    }
}