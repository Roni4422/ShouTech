using System.Data;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using ShouTech.Domain.Entities.Navigation;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Core.Navigation;

public sealed class NavigationRepository : INavigationRepository
{
    private readonly IDbConnection _db;

    public NavigationRepository(IDbConnection db) => _db = db;

    public async Task<IReadOnlyList<MainMenuItem>> GetMainMenuItemsAsync(
        int userId,
        int tenantId = 1,
        int companyId = 1,
        CancellationToken cancellationToken = default)
    {
        const string sql = "sys.sp_GetMainMenuItems";

        var command = new CommandDefinition(
            sql,
            new { UserID = userId, TenantID = tenantId, CompanyID = companyId },
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken);

        var rows = await _db.QueryAsync<MainMenuItem>(command).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<RibbonLayoutRow>> GetRibbonLayoutAsync(
        int userId,
        string? tabCode = null,
        int tenantId = 1,
        int companyId = 1,
        CancellationToken cancellationToken = default)
    {
        const string sql = "sys.sp_GetUserRibbonLayout";

        var command = new CommandDefinition(
            sql,
            new { UserID = userId, TabCode = tabCode, TenantID = tenantId, CompanyID = companyId },
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken);

        var rows = await _db.QueryAsync<RibbonLayoutRow>(command).ConfigureAwait(false);
        return rows.AsList();
    }
}