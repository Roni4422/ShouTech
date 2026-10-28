// cod/cor/invmod.cs
using System;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using ShouTech.Core.Security;

namespace ShouTech.Core.Inv
{
    public class InventoryModule : IInventoryService, IDisposable
    {
        private readonly UnitOfWork _uow;
        private readonly IMemoryCache _cache;
        private readonly ILogger<InventoryModule> _logger;
        private readonly ISecurityEngine _security;

        public InventoryModule(IDbConnection db, IMemoryCache cache, ILogger<InventoryModule> logger, ISecurityEngine security)
        {
            _uow = new UnitOfWork(db);
            _cache = cache;
            _logger = logger;
            _security = security;
        }

        public async Task<bool> ProcessPOSSaleAsync(POSSaleRequest req)
        {
            await _security.ValidatePermissionAsync(req.UserId, "POS.ProcessSale");

            _uow.Begin();
            try
            {
                _uow.Commit();
                _logger.LogInformation("POS Sale Completed Successfully");
                return true;
            }
            catch (Exception ex)
            {
                _uow.Rollback();
                _logger.LogError(ex, "POS Sale Failed");
                throw;
            }
        }

        public void Dispose() => _uow?.Dispose();
    }
}