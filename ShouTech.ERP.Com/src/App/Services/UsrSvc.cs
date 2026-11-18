// shoutech_erp_v0.1.6/src/ShouTech.Application/Services/UsrSvc.cs
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

public sealed class UsrSvc
{
    private readonly IUsrRepo _repo;   // The repository contract will be wired up later.
    private readonly ILicProv _lic;

    public UsrSvc(IUsrRepo repo, ILicProv lic)
    {
        _repo = repo;
        _lic = lic;
    }

    public async Task<Usr> CreateAsync(
        string userName,
        string displayName,
        Guid createdBy,
        string? email = null,
        string? mobile = null,
        string lang = "en",
        CancellationToken ct = default)
    {
        var lic = await _lic.ValidateAsync(ct);
        if (!lic.IsUsable)
            throw new InvalidOperationException("License is not usable.");


        var user = Usr.Create(userName, displayName, createdBy, email, mobile, lang);
        await _repo.AddAsync(user, ct);
        return user;
    }

    public async Task SetActiveAsync(Guid id, bool isActive, Guid modifiedBy, CancellationToken ct = default)
    {
        var user = await _repo.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException("User not found.");

        user.SetActive(isActive, modifiedBy);
        await _repo.UpdateAsync(user, ct);
    }
}