// shoutech_erp_v0.1.6/src/ShouTech.Application/Services/FYearSvc.cs
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

public sealed class FYearSvc
{
    private readonly IFYearRepo _repo;
    private readonly IComRepo _comRepo;
    private readonly ILicProv _lic;

    public FYearSvc(IFYearRepo repo, IComRepo comRepo, ILicProv lic)
    {
        _repo = repo;
        _comRepo = comRepo;
        _lic = lic;
    }

    public async Task<FYear> CreateAsync(
        Guid companyId,
        string name,
        DateOnly start,
        DateOnly end,
        string dbName,
        Guid userId,
        bool setAsCurrent = false,
        CancellationToken ct = default)
    {
        var lic = await _lic.ValidateAsync(ct);
        if (!lic.IsUsable)
            throw new InvalidOperationException("License is not usable.");

        var company = await _comRepo.GetByIdAsync(companyId, false, ct)
            ?? throw new InvalidOperationException("Company not found.");

        var year = FYear.Create(companyId, name, start, end, dbName, userId, setAsCurrent);

        if (setAsCurrent)
        {
            await _repo.ClearCurrentForCompanyAsync(companyId, userId, ct);
        }

        await _repo.AddAsync(year, ct);
        return year;
    }

    public async Task CloseAsync(Guid id, Guid userId, CancellationToken ct = default)
    {
        var year = await _repo.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException("Fiscal year not found.");

        if (year.IsClosed)
            throw new InvalidOperationException("Fiscal year is already closed.");

        year.Close(userId);
        await _repo.UpdateAsync(year, ct);
    }
}