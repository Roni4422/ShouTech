using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.Domain.Entities.Navigation;

namespace ShouTech.Domain.Interfaces;

public interface INavigationRepository
{
    Task<IReadOnlyList<MainMenuItem>> GetMainMenuItemsAsync(
        int userId,
        int tenantId = 1,
        int companyId = 1,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RibbonLayoutRow>> GetRibbonLayoutAsync(
        int userId,
        string? tabCode = null,
        int tenantId = 1,
        int companyId = 1,
        CancellationToken cancellationToken = default);
}