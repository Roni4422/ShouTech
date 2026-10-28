// cod/cor/Models.cs
using System;

namespace ShouTech.Core.Inv
{
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
}