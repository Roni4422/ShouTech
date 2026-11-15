using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

public sealed class PartyService
{
    private readonly IPartyRepository _repository;

    public PartyService(IPartyRepository repository)
        => _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public Task<IReadOnlyList<Party>> GetAsync(Guid companyId, string kind, bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        var normalizedKind = NormalizeKind(kind);
        return _repository.GetAsync(companyId, normalizedKind, includeInactive, cancellationToken);
    }

    public Task SaveAsync(PartyDraft party, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(party);
        if (party.CompanyId == Guid.Empty) throw new ArgumentException("Company is required.", nameof(party));
        var code = party.Code?.Trim();
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Party code is required.", nameof(party));
        if (string.IsNullOrWhiteSpace(party.NameEnglish)) throw new ArgumentException("An English party name is required.", nameof(party));
        if (party.CreditLimit < 0) throw new ArgumentOutOfRangeException(nameof(party), "Credit limit cannot be negative.");
        if (party.CreditDays < 0) throw new ArgumentOutOfRangeException(nameof(party), "Credit days cannot be negative.");
        var normalized = party with {
            Code = code,
            Kind = NormalizeKind(party.Kind),
            NameEnglish = party.NameEnglish.Trim(),
            NameArabic = Clean(party.NameArabic),
            GroupCode = Clean(party.GroupCode),
            TaxNumber = Clean(party.TaxNumber),
            CommercialRegistration = Clean(party.CommercialRegistration),
            PriceTier = Clean(party.PriceTier),
            GlAccountCode = Clean(party.GlAccountCode),
            Phone = Clean(party.Phone),
            Mobile = Clean(party.Mobile),
            Email = Clean(party.Email),
            Address = Clean(party.Address),
            Actor = Clean(party.Actor)
        };
        return _repository.SaveAsync(normalized, cancellationToken);
    }

    private static string NormalizeKind(string kind)
    {
        var value = kind?.Trim().ToUpperInvariant();
        return value is "C" or "S" or "B" ? value : throw new ArgumentException("Party kind must be C, S, or B.", nameof(kind));
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
