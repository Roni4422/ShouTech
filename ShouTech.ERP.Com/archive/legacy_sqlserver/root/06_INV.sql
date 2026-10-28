-- ============================================================================
-- File: dba/06_INV.sql
-- Module: Inventory, Multi-Warehouse, Bins, Batches, Serials & Landed Costs
-- Architecture Grade: Hyperscale Enterprise (Real-time Valuation & Traceability)
-- ============================================================================

CREATE TABLE UnitsOfMeasure (
    UOMID INT IDENTITY(1,1) PRIMARY KEY,
    UOMCode NVARCHAR(10) NOT NULL UNIQUE, -- PCS, BOX, KG, MTR
    UOMNameAr NVARCHAR(50) NOT NULL,
    UOMNameEn NVARCHAR(50) NULL
);

CREATE TABLE UOMConversions (
    ConversionID INT IDENTITY(1,1) PRIMARY KEY,
    FromUOMID INT NOT NULL FOREIGN KEY REFERENCES UnitsOfMeasure(UOMID),
    ToUOMID INT NOT NULL FOREIGN KEY REFERENCES UnitsOfMeasure(UOMID),
    ConversionFactor DECIMAL(18,6) NOT NULL CHECK (ConversionFactor > 0),
    CONSTRAINT UQ_UOMConversion UNIQUE (FromUOMID, ToUOMID)
);

CREATE TABLE Categories (
    CategoryID INT IDENTITY(1,1) PRIMARY KEY,
    CategoryCode NVARCHAR(20) NOT NULL UNIQUE,
    CategoryNameAr NVARCHAR(150) NOT NULL,
    CategoryNameEn NVARCHAR(150) NULL,
    ParentCategoryID INT NULL FOREIGN KEY REFERENCES Categories(CategoryID),
    IsActive BIT DEFAULT 1
);

CREATE TABLE Products (
    ProductID BIGINT IDENTITY(1,1) PRIMARY KEY,
    ProductCode NVARCHAR(50) NOT NULL UNIQUE,
    ProductNameAr NVARCHAR(250) NOT NULL,
    ProductNameEn NVARCHAR(250) NULL,
    CategoryID INT NOT NULL FOREIGN KEY REFERENCES Categories(CategoryID),
    BaseUOMID INT NOT NULL FOREIGN KEY REFERENCES UnitsOfMeasure(UOMID),
    
    CostingMethod NVARCHAR(20) NOT NULL DEFAULT 'AVCO', -- AVCO (المتوسط المتحرك), FIFO, Standard
    TrackingType NVARCHAR(20) NOT NULL DEFAULT 'None', -- None, Batch, Serial
    
    StandardCost DECIMAL(18,4) DEFAULT 0.0000,
    MovingAverageCost DECIMAL(18,4) DEFAULT 0.0000,
    SellingPrice DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    MinSellingPrice DECIMAL(18,4) DEFAULT 0.0000,
    
    ReorderLevel DECIMAL(18,4) DEFAULT 0.0000,
    MaxStockLevel DECIMAL(18,4) DEFAULT 0.0000,
    TaxRate DECIMAL(5,2) DEFAULT 15.00,
    
    InventoryAccountID INT NOT NULL FOREIGN KEY REFERENCES Accounts(AccountID),
    COGSAccountID INT NOT NULL FOREIGN KEY REFERENCES Accounts(AccountID), -- حساب تكلفة البضاعة المباعة
    SalesAccountID INT NOT NULL FOREIGN KEY REFERENCES Accounts(AccountID),
    
    IsActive BIT DEFAULT 1,
    IsDeleted BIT DEFAULT 0,
    CreatedAt DATETIME2 DEFAULT GETDATE(),
    CreatedBy INT NOT NULL FOREIGN KEY REFERENCES Users(UserID)
);

CREATE TABLE ProductBarcodes (
    BarcodeID BIGINT IDENTITY(1,1) PRIMARY KEY,
    ProductID BIGINT NOT NULL FOREIGN KEY REFERENCES Products(ProductID) ON DELETE CASCADE,
    Barcode NVARCHAR(100) NOT NULL UNIQUE,
    UOMID INT NOT NULL FOREIGN KEY REFERENCES UnitsOfMeasure(UOMID),
    IsPrimary BIT DEFAULT 0
);

CREATE TABLE Warehouses (
    WarehouseID INT IDENTITY(1,1) PRIMARY KEY,
    WarehouseCode NVARCHAR(20) NOT NULL UNIQUE,
    BranchID INT NOT NULL FOREIGN KEY REFERENCES Branches(BranchID),
    WarehouseNameAr NVARCHAR(150) NOT NULL,
    WarehouseNameEn NVARCHAR(150) NULL,
    IsActive BIT DEFAULT 1
);

CREATE TABLE WarehouseBins (
    BinID INT IDENTITY(1,1) PRIMARY KEY,
    WarehouseID INT NOT NULL FOREIGN KEY REFERENCES Warehouses(WarehouseID),
    BinCode NVARCHAR(30) NOT NULL, -- e.g. A1-R02-S04
    ZoneName NVARCHAR(50) NULL,
    IsActive BIT DEFAULT 1,
    CONSTRAINT UQ_WarehouseBin UNIQUE (WarehouseID, BinCode)
);

CREATE TABLE ProductBatches (
    BatchID BIGINT IDENTITY(1,1) PRIMARY KEY,
    ProductID BIGINT NOT NULL FOREIGN KEY REFERENCES Products(ProductID),
    WarehouseID INT NOT NULL FOREIGN KEY REFERENCES Warehouses(WarehouseID),
    BatchNo NVARCHAR(50) NOT NULL,
    ProductionDate DATE NULL,
    ExpiryDate DATE NOT NULL,
    QuantityOnHand DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    IsLocked BIT DEFAULT 0,
    CONSTRAINT UQ_ProductBatch UNIQUE (ProductID, WarehouseID, BatchNo)
);

CREATE TABLE ProductSerials (
    SerialID BIGINT IDENTITY(1,1) PRIMARY KEY,
    ProductID BIGINT NOT NULL FOREIGN KEY REFERENCES Products(ProductID),
    WarehouseID INT NOT NULL FOREIGN KEY REFERENCES Warehouses(WarehouseID),
    SerialNumber NVARCHAR(100) NOT NULL UNIQUE,
    Status NVARCHAR(20) DEFAULT 'InStock', -- InStock, Sold, Scrapped, Returned
    InboundDate DATETIME2 DEFAULT GETDATE()
);

CREATE TABLE StockTransfers (
    TransferID BIGINT IDENTITY(1,1) PRIMARY KEY,
    TransferNo NVARCHAR(30) NOT NULL UNIQUE,
    FromWarehouseID INT NOT NULL FOREIGN KEY REFERENCES Warehouses(WarehouseID),
    ToWarehouseID INT NOT NULL FOREIGN KEY REFERENCES Warehouses(WarehouseID),
    TransferDate DATE NOT NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT 'Pending', -- Pending, InTransit, Received, Cancelled
    Notes NVARCHAR(300) NULL,
    CreatedBy INT NOT NULL FOREIGN KEY REFERENCES Users(UserID),
    ReceivedBy INT NULL FOREIGN KEY REFERENCES Users(UserID),
    CreatedAt DATETIME2 DEFAULT GETDATE()
);

CREATE TABLE StockTransferLines (
    LineID BIGINT IDENTITY(1,1) PRIMARY KEY,
    TransferID BIGINT NOT NULL FOREIGN KEY REFERENCES StockTransfers(TransferID) ON DELETE CASCADE,
    ProductID BIGINT NOT NULL FOREIGN KEY REFERENCES Products(ProductID),
    Quantity DECIMAL(18,4) NOT NULL CHECK (Quantity > 0),
    BatchNo NVARCHAR(50) NULL,
    LineNo INT NOT NULL
);

CREATE TABLE StockAdjustments (
    AdjustmentID BIGINT IDENTITY(1,1) PRIMARY KEY,
    AdjustmentNo NVARCHAR(30) NOT NULL UNIQUE,
    WarehouseID INT NOT NULL FOREIGN KEY REFERENCES Warehouses(WarehouseID),
    AdjustmentDate DATE NOT NULL,
    Reason NVARCHAR(250) NOT NULL,
    JournalHeaderID BIGINT NULL FOREIGN KEY REFERENCES JournalHeaders(JournalHeaderID),
    Status NVARCHAR(20) DEFAULT 'Draft', -- Draft, Approved
    CreatedBy INT NOT NULL FOREIGN KEY REFERENCES Users(UserID),
    ApprovedBy INT NULL FOREIGN KEY REFERENCES Users(UserID),
    CreatedAt DATETIME2 DEFAULT GETDATE()
);

CREATE TABLE StockAdjustmentLines (
    LineID BIGINT IDENTITY(1,1) PRIMARY KEY,
    AdjustmentID BIGINT NOT NULL FOREIGN KEY REFERENCES StockAdjustments(AdjustmentID) ON DELETE CASCADE,
    ProductID BIGINT NOT NULL FOREIGN KEY REFERENCES Products(ProductID),
    SystemQty DECIMAL(18,4) NOT NULL, -- الكمية بالنظام
    PhysicalQty DECIMAL(18,4) NOT NULL, -- الكمية الفعلية بالجرد
    VarianceQty AS (PhysicalQty - SystemQty), -- الفارق (+ أو -)
    UnitCost DECIMAL(18,4) NOT NULL,
    TotalVarianceValue AS ((PhysicalQty - SystemQty) * UnitCost),
    LineNo INT NOT NULL
);

CREATE TABLE LandedCostAllocations (
    AllocationID BIGINT IDENTITY(1,1) PRIMARY KEY,
    PurchaseInvoiceID BIGINT NOT NULL FOREIGN KEY REFERENCES PurchaseInvoices(InvoiceID),
    ExpenseType NVARCHAR(100) NOT NULL, -- Freight, Customs, Insurance, Handling
    ExpenseAmount DECIMAL(18,4) NOT NULL CHECK (ExpenseAmount > 0),
    AllocationMethod NVARCHAR(30) NOT NULL DEFAULT 'ByValue', -- ByValue, ByQuantity, ByWeight
    CreatedBy INT NOT NULL FOREIGN KEY REFERENCES Users(UserID),
    CreatedAt DATETIME2 DEFAULT GETDATE()
);

CREATE INDEX IX_Products_CategoryID ON Products(CategoryID);
CREATE INDEX IX_ProductBarcodes_Barcode ON ProductBarcodes(Barcode);
CREATE INDEX IX_ProductBatches_ExpiryDate ON ProductBatches(ExpiryDate);
CREATE INDEX IX_ProductSerials_SerialNumber ON ProductSerials(SerialNumber);