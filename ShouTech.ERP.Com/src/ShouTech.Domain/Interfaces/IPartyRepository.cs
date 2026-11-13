using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

public interface IPartyRepository
{
    Task<IReadOnlyList<Party>> GetAsync(Guid companyId, string kind, bool includeInactive = false, CancellationToken cancellationToken = default);
    Task SaveAsync(PartyDraft party, CancellationToken cancellationToken = default);
}
