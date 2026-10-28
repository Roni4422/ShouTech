// cod/cor/invrep.cs
using Dapper;
using System.Data;

namespace ShouTech.Core.Inv
{
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
}