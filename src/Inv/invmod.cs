// cod/cor/invmod.cs

using System;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using ShouTech.Core.Security;
using Dapper;
using System.Collections.Generic;

namespace ShouTech.Core.Inv
{
// ---- merged from cod/cor/Models.cs ----
    public class POSSaleRequest
    {
        public Guid UserId { get; set; }
        public Guid CompanyId { get; set; }
        public string Barcode { get; set; } = string.Empty;
        public int WarehouseId { get; set; }
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
    }

    public class ItemDto
    {
        public int ItemID { get; set; }
        public Guid CompanyID { get; set; }
        public string Barcode { get; set; } = string.Empty;
        public string ItemName { get; set; } = string.Empty;
        public decimal AvailableQty { get; set; }
        public bool IsActive { get; set; }
        public bool IsDeleted { get; set; }
    }

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

// ---- merged from cod/cor/invrep.cs ----
    public class InventoryRepository
    {
        private readonly IDbConnection _db;
        public InventoryRepository(IDbConnection db) => _db = db;

        public async Task<ItemDto?> GetItemByBarcodeAsync(string barcode, Guid companyId)
        {
            const string sql = "SELECT * FROM dbo.Items WHERE CompanyID=@cid AND Barcode=@b AND IsActive=1 AND IsDeleted=0";
            return await _db.QueryFirstOrDefaultAsync<ItemDto>(sql, new { cid = companyId, b = barcode });
        }

        public async Task UpdateStockAsync(int itemId, int whId, decimal qty, string type, Guid companyId)
        {
            // Atomic update + movement
            await _db.ExecuteAsync(@"
                UPDATE dbo.StockBalances SET AvailableQty += @qty WHERE ItemID=@item AND WarehouseID=@wh;
                INSERT INTO dbo.StockMovements (CompanyID,MovementType,ItemID,Quantity,WarehouseIDFrom) 
                VALUES (@cid,@type,@item,@qty,@wh);", 
                new { item = itemId, wh = whId, qty, type, cid = companyId });
        }
    }

// ---- merged from cod/cor/invsvc.cs ----
    public interface IInventoryService
    {
        Task<bool> ProcessPOSSaleAsync(POSSaleRequest req);
    }

// ---- merged from cod/cor/invuow.cs ----
    public class UnitOfWork : IDisposable
    {
        private readonly IDbConnection _conn;
        private IDbTransaction? _tx;

        public InventoryRepository Inv { get; }

        public UnitOfWork(IDbConnection conn)
        {
            _conn = conn;
            Inv = new InventoryRepository(conn);
        }

        public void Begin() => _tx = _conn.BeginTransaction();
        public void Commit() => _tx?.Commit();
        public void Rollback() => _tx?.Rollback();
        public void Dispose() { _tx?.Dispose(); _conn?.Dispose(); }
    }
}
