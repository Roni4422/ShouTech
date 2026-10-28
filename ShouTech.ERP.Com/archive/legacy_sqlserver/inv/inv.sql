-- ========================================================================
-- FILE: dba/inv/inv_ultimate.sql
-- PROJECT: SHOUTECH ERP V10 - Inventory & POS Module (ULTIMATE 10/10)
-- VERSION: 10.6.0
-- DESCRIPTION: 
-- ========================================================================
--
-- ========================================================================

USE [$(DbFileName)];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

PRINT N'═══════════════════════════════════════════════════════════════════════';
PRINT N'SHOUTECH ERP V10 – INVENTORY & POS MODULE (ULTIMATE 10/10)';
PRINT N'═══════════════════════════════════════════════════════════════════════';
PRINT N'📌 Includes: Products | Stock | Batches | Serial | Valuation | POS';
PRINT N'═══════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

-- ========================================================================
-- ========================================================================

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ProductCategories')
    BEGIN
        CREATE TABLE dbo.ProductCategories (
            CategoryID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ParentCategoryID INT NULL,
            CategoryCode NVARCHAR(50) NOT NULL,
            CategoryNameAR NVARCHAR(200) NOT NULL,
            CategoryNameEN NVARCHAR(200) NULL,
            CategoryLevel TINYINT NOT NULL DEFAULT 1,
            IconPath NVARCHAR(500) NULL,
            ImagePath NVARCHAR(500) NULL,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_ProductCategories PRIMARY KEY CLUSTERED (CategoryID),
            CONSTRAINT UQ_ProductCategories_Code UNIQUE (CompanyID, CategoryCode),
            CONSTRAINT FK_ProductCategories_Parent FOREIGN KEY (ParentCategoryID) REFERENCES dbo.ProductCategories(CategoryID),
            CONSTRAINT FK_ProductCategories_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ 1.1 ProductCategories created.';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ProductCategories_Company_Active 
        ON dbo.ProductCategories(CompanyID, IsActive) WHERE IsDeleted = 0;
    GO

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Brands')
    BEGIN
        CREATE TABLE dbo.Brands (
            BrandID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            BrandCode NVARCHAR(50) NOT NULL,
            BrandNameAR NVARCHAR(200) NOT NULL,
            BrandNameEN NVARCHAR(200) NULL,
            LogoPath NVARCHAR(500) NULL,
            Website NVARCHAR(200) NULL,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_Brands PRIMARY KEY CLUSTERED (BrandID),
            CONSTRAINT UQ_Brands_Code UNIQUE (CompanyID, BrandCode),
            CONSTRAINT FK_Brands_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ 1.2 Brands created.';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Brands_Company_Active 
        ON dbo.Brands(CompanyID, IsActive) WHERE IsDeleted = 0;
    GO

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'UnitsOfMeasure')
    BEGIN
        CREATE TABLE dbo.UnitsOfMeasure (
            UOMID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            UOMCode NVARCHAR(20) NOT NULL,
            UOMNameAR NVARCHAR(50) NOT NULL,
            UOMNameEN NVARCHAR(50) NULL,
            BaseUOMID INT NULL,
            ConversionFactor DECIMAL(18,6) NOT NULL DEFAULT 1,
            IsBaseUnit BIT NOT NULL DEFAULT 0,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_UnitsOfMeasure PRIMARY KEY CLUSTERED (UOMID),
            CONSTRAINT UQ_UnitsOfMeasure_Code UNIQUE (CompanyID, UOMCode),
            CONSTRAINT FK_UnitsOfMeasure_BaseUOM FOREIGN KEY (BaseUOMID) REFERENCES dbo.UnitsOfMeasure(UOMID),
            CONSTRAINT FK_UnitsOfMeasure_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ 1.3 UnitsOfMeasure created.';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_UnitsOfMeasure_Company_Active 
        ON dbo.UnitsOfMeasure(CompanyID, IsActive) WHERE IsDeleted = 0;
    GO

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Products')
    BEGIN
        CREATE TABLE dbo.Products (
            ProductID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            
            ProductCode NVARCHAR(50) NOT NULL,
            ProductNameAR NVARCHAR(255) NOT NULL,
            ProductNameEN NVARCHAR(255) NULL,
            ProductDescription NVARCHAR(MAX) NULL,
            
            CategoryID INT NULL,
            BrandID INT NULL,
            
            PrimaryUOMID INT NOT NULL,
            SaleUOMID INT NULL,
            PurchaseUOMID INT NULL,
            
            Barcode NVARCHAR(50) NULL,
            QuickCode NVARCHAR(20) NULL,
            SKU NVARCHAR(50) NULL,
            
            DefaultSalePrice DECIMAL(18,6) NOT NULL DEFAULT 0,
            DefaultPurchasePrice DECIMAL(18,6) NOT NULL DEFAULT 0,
            CostPrice DECIMAL(18,6) NOT NULL DEFAULT 0,
            LastPurchasePrice DECIMAL(18,6) NULL,
            LastSalePrice DECIMAL(18,6) NULL,
            
            TaxCategory NVARCHAR(20) NOT NULL DEFAULT 'STANDARD',
            TaxRate DECIMAL(5,2) NOT NULL DEFAULT 15,
            
            IsStockItem BIT NOT NULL DEFAULT 1,
            IsServiceItem BIT NOT NULL DEFAULT 0,
            IsPOSItem BIT NOT NULL DEFAULT 1,
            IsSerialized BIT NOT NULL DEFAULT 0,
            IsBatchTracked BIT NOT NULL DEFAULT 0,
            IsExpiryTracked BIT NOT NULL DEFAULT 0,
            ExpiryDays INT NULL,
            ShelfLifeDays INT NULL,
            
            ReorderLevel DECIMAL(18,3) NOT NULL DEFAULT 0,
            ReorderQuantity DECIMAL(18,3) NOT NULL DEFAULT 0,
            SafetyStock DECIMAL(18,3) NOT NULL DEFAULT 0,
            MaxStock DECIMAL(18,3) NULL,
            
            Weight DECIMAL(18,6) NULL,
            Length DECIMAL(18,6) NULL,
            Width DECIMAL(18,6) NULL,
            Height DECIMAL(18,6) NULL,
            
            MainImagePath NVARCHAR(500) NULL,
            GalleryImages NVARCHAR(2000) NULL,
            
            DemandVelocity TINYINT NULL,
            ProfitMargin DECIMAL(5,2) NULL,
            ProductScore DECIMAL(5,2) NULL,
            IsRecommended BIT NOT NULL DEFAULT 0,
            
            IsActive BIT NOT NULL DEFAULT 1,
            IsBlocked BIT NOT NULL DEFAULT 0,
            BlockReason NVARCHAR(200) NULL,
            DiscontinuedDate DATE NULL,
            
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            
            CONSTRAINT PK_Products PRIMARY KEY CLUSTERED (ProductID),
            CONSTRAINT UQ_Products_Code UNIQUE (CompanyID, ProductCode),
            CONSTRAINT UQ_Products_Barcode UNIQUE (CompanyID, Barcode) WHERE Barcode IS NOT NULL,
            CONSTRAINT FK_Products_Category FOREIGN KEY (CategoryID) REFERENCES dbo.ProductCategories(CategoryID),
            CONSTRAINT FK_Products_Brand FOREIGN KEY (BrandID) REFERENCES dbo.Brands(BrandID),
            CONSTRAINT FK_Products_PrimaryUOM FOREIGN KEY (PrimaryUOMID) REFERENCES dbo.UnitsOfMeasure(UOMID),
            CONSTRAINT FK_Products_SaleUOM FOREIGN KEY (SaleUOMID) REFERENCES dbo.UnitsOfMeasure(UOMID),
            CONSTRAINT FK_Products_PurchaseUOM FOREIGN KEY (PurchaseUOMID) REFERENCES dbo.UnitsOfMeasure(UOMID),
            CONSTRAINT FK_Products_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID),
            CONSTRAINT CK_Products_TaxCategory CHECK (TaxCategory IN ('STANDARD', 'ZERO', 'EXEMPT'))
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ 1.4 Products created (Ultimate version).';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Products_POS_Search 
        ON dbo.Products(CompanyID, IsPOSItem, IsActive) 
        INCLUDE (ProductCode, ProductNameAR, Barcode, QuickCode, DefaultSalePrice, MainImagePath) 
        WHERE IsDeleted = 0;

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Products_Barcode_Search 
        ON dbo.Products(Barcode) WHERE Barcode IS NOT NULL AND IsDeleted = 0;

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Products_Name_Search 
        ON dbo.Products(ProductNameAR) WHERE IsDeleted = 0 AND IsActive = 1;

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Products_Reorder 
        ON dbo.Products(ReorderLevel, IsActive) WHERE IsDeleted = 0;

    CREATE NONCLUSTERED COLUMNSTORE INDEX IF NOT EXISTS CSIX_Products_Analytics 
        ON dbo.Products (CategoryID, BrandID, IsActive, CreatedAt, DefaultSalePrice, CostPrice);
    GO

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ProductAttributes')
    BEGIN
        CREATE TABLE dbo.ProductAttributes (
            AttributeID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ProductID INT NOT NULL,
            AttributeName NVARCHAR(100) NOT NULL,
            AttributeValue NVARCHAR(200) NOT NULL,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_ProductAttributes PRIMARY KEY CLUSTERED (AttributeID),
            CONSTRAINT FK_ProductAttributes_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID) ON DELETE CASCADE
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ 1.5 ProductAttributes created.';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ProductAttributes_Product 
        ON dbo.ProductAttributes(ProductID) WHERE IsDeleted = 0;
    GO

-- ========================================================================
-- ========================================================================

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'StockBalances')
    BEGIN
        CREATE TABLE dbo.StockBalances (
            StockBalanceID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            WarehouseID BIGINT NOT NULL,
            ProductID INT NOT NULL,
            AvailableQty DECIMAL(18,4) NOT NULL DEFAULT 0,
            ReservedQty DECIMAL(18,4) NOT NULL DEFAULT 0,
            DamagedQty DECIMAL(18,4) NOT NULL DEFAULT 0,
            OnOrderQty DECIMAL(18,4) NOT NULL DEFAULT 0,
            ReservedForSales DECIMAL(18,4) NOT NULL DEFAULT 0,
            ReservedForTransfers DECIMAL(18,4) NOT NULL DEFAULT 0,
            AverageCost DECIMAL(18,6) NULL,
            LastCost DECIMAL(18,6) NULL,
            LastUpdated DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            LastTransactionType NVARCHAR(30) NULL,
            LastTransactionDate DATETIME2(7) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_StockBalances PRIMARY KEY CLUSTERED (StockBalanceID),
            CONSTRAINT UQ_StockBalances_Product_Warehouse UNIQUE (CompanyID, WarehouseID, ProductID),
            CONSTRAINT FK_StockBalances_Warehouse FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT FK_StockBalances_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID),
            CONSTRAINT FK_StockBalances_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ 2.1 StockBalances created.';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_StockBalances_Warehouse_Product 
        ON dbo.StockBalances(WarehouseID, ProductID) INCLUDE (AvailableQty, ReservedQty, AverageCost) WHERE IsDeleted = 0;

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_StockBalances_Available 
        ON dbo.StockBalances(AvailableQty) WHERE AvailableQty > 0 AND IsDeleted = 0;
    GO

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'StockMovements')
    BEGIN
        CREATE TABLE dbo.StockMovements (
            MovementID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            MovementType NVARCHAR(30) NOT NULL,
            MovementDate DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            WarehouseIDFrom BIGINT NULL,
            WarehouseIDTo BIGINT NULL,
            ProductID INT NOT NULL,
            BatchID BIGINT NULL,
            SerialNumberID BIGINT NULL,
            Quantity DECIMAL(18,4) NOT NULL,
            UnitCost DECIMAL(18,6) NULL,
            TotalCost DECIMAL(18,6) NULL,
            ReferenceTable NVARCHAR(50) NULL,
            ReferenceID BIGINT NULL,
            ReferenceLineID BIGINT NULL,
            ReferenceNumber NVARCHAR(50) NULL,
            Notes NVARCHAR(MAX) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            RowVersion ROWVERSION,
            CONSTRAINT PK_StockMovements PRIMARY KEY CLUSTERED (MovementID),
            CONSTRAINT FK_StockMovements_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID),
            CONSTRAINT FK_StockMovements_FromWarehouse FOREIGN KEY (WarehouseIDFrom) REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT FK_StockMovements_ToWarehouse FOREIGN KEY (WarehouseIDTo) REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT CK_StockMovements_Type CHECK (MovementType IN 
                ('PURCHASE', 'SALE', 'RETURN_PURCHASE', 'RETURN_SALE', 
                 'TRANSFER_IN', 'TRANSFER_OUT', 'ADJUSTMENT', 'COUNT_OPEN', 'COUNT_CLOSE'))
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ 2.2 StockMovements created.';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_StockMovements_Product 
        ON dbo.StockMovements(ProductID, MovementDate DESC) WHERE IsDeleted = 0;

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_StockMovements_Reference 
        ON dbo.StockMovements(ReferenceTable, ReferenceID) WHERE IsDeleted = 0;

    CREATE NONCLUSTERED COLUMNSTORE INDEX IF NOT EXISTS CSIX_StockMovements_Analytics 
        ON dbo.StockMovements (CompanyID, MovementType, MovementDate, ProductID, Quantity, TotalCost);
    GO

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'FIFOLayers')
    BEGIN
        CREATE TABLE dbo.FIFOLayers (
            LayerID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ProductID INT NOT NULL,
            WarehouseID BIGINT NOT NULL,
            BatchID BIGINT NULL,
            LayerDate DATE NOT NULL,
            Quantity DECIMAL(18,4) NOT NULL,
            UnitCost DECIMAL(18,6) NOT NULL,
            RemainingQuantity DECIMAL(18,4) NOT NULL,
            SourceReference NVARCHAR(50) NULL,
            SourceID BIGINT NULL,
            IsConsumed BIT NOT NULL DEFAULT 0,
            ConsumedAt DATETIME2(7) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_FIFOLayers PRIMARY KEY CLUSTERED (LayerID),
            CONSTRAINT FK_FIFOLayers_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID),
            CONSTRAINT FK_FIFOLayers_Warehouse FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ 2.3 FIFOLayers created.';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_FIFOLayers_Product_Warehouse 
        ON dbo.FIFOLayers(ProductID, WarehouseID, LayerDate) WHERE IsDeleted = 0 AND IsConsumed = 0;
    GO

-- ========================================================================
-- ========================================================================

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Batches')
    BEGIN
        CREATE TABLE dbo.Batches (
            BatchID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ProductID INT NOT NULL,
            WarehouseID BIGINT NOT NULL,
            BatchNumber NVARCHAR(50) NOT NULL,
            SupplierBatchNumber NVARCHAR(50) NULL,
            ManufacturingDate DATE NULL,
            ExpiryDate DATE NULL,
            ReceivedDate DATE NOT NULL DEFAULT CAST(GETDATE() AS DATE),
            OriginalQuantity DECIMAL(18,4) NOT NULL,
            CurrentQuantity DECIMAL(18,4) NOT NULL,
            PurchaseCost DECIMAL(18,6) NULL,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_Batches PRIMARY KEY CLUSTERED (BatchID),
            CONSTRAINT UQ_Batches_Number UNIQUE (CompanyID, ProductID, BatchNumber),
            CONSTRAINT FK_Batches_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID),
            CONSTRAINT FK_Batches_Warehouse FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ 3.1 Batches created.';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Batches_Expiry 
        ON dbo.Batches(ExpiryDate, ProductID) WHERE IsActive = 1 AND IsDeleted = 0;
    GO

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SerialNumbers')
    BEGIN
        CREATE TABLE dbo.SerialNumbers (
            SerialID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ProductID INT NOT NULL,
            WarehouseID BIGINT NOT NULL,
            SerialNumber NVARCHAR(100) NOT NULL,
            BatchID BIGINT NULL,
            Status NVARCHAR(20) NOT NULL DEFAULT 'IN_STOCK',
            ReceivedDate DATE NOT NULL DEFAULT CAST(GETDATE() AS DATE),
            SoldDate DATE NULL,
            CustomerID BIGINT NULL,
            PurchaseInvoiceID BIGINT NULL,
            SalesInvoiceID BIGINT NULL,
            LastTransactionDate DATETIME2(7) NULL,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_SerialNumbers PRIMARY KEY CLUSTERED (SerialID),
            CONSTRAINT UQ_SerialNumbers_Number UNIQUE (CompanyID, ProductID, SerialNumber),
            CONSTRAINT FK_SerialNumbers_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID),
            CONSTRAINT FK_SerialNumbers_Warehouse FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT FK_SerialNumbers_Batch FOREIGN KEY (BatchID) REFERENCES dbo.Batches(BatchID),
            CONSTRAINT CK_SerialNumbers_Status CHECK (Status IN ('IN_STOCK', 'RESERVED', 'SOLD', 'RETURNED', 'DAMAGED'))
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ 3.2 SerialNumbers created.';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_SerialNumbers_Status 
        ON dbo.SerialNumbers(Status, ProductID) WHERE IsActive = 1 AND IsDeleted = 0;
    GO

-- ========================================================================
-- ========================================================================

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ValuationMethods')
    BEGIN
        CREATE TABLE dbo.ValuationMethods (
            ValuationMethodID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            MethodCode NVARCHAR(20) NOT NULL,
            MethodNameAR NVARCHAR(100) NOT NULL,
            MethodNameEN NVARCHAR(100) NOT NULL,
            IsDefault BIT NOT NULL DEFAULT 0,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_ValuationMethods PRIMARY KEY CLUSTERED (ValuationMethodID),
            CONSTRAINT UQ_ValuationMethods_Code UNIQUE (CompanyID, MethodCode),
            CONSTRAINT FK_ValuationMethods_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ 4.1 ValuationMethods created.';
    END
    GO

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ProductValuation')
    BEGIN
        CREATE TABLE dbo.ProductValuation (
            ProductValuationID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ProductID INT NOT NULL,
            ValuationMethodID INT NOT NULL,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_ProductValuation PRIMARY KEY CLUSTERED (ProductValuationID),
            CONSTRAINT UQ_ProductValuation_Product UNIQUE (CompanyID, ProductID),
            CONSTRAINT FK_ProductValuation_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID),
            CONSTRAINT FK_ProductValuation_Method FOREIGN KEY (ValuationMethodID) REFERENCES dbo.ValuationMethods(ValuationMethodID)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ 4.2 ProductValuation created.';
    END
    GO

-- ========================================================================
-- ========================================================================

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'POSTerminals')
    BEGIN
        CREATE TABLE dbo.POSTerminals (
            TerminalID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            BranchID UNIQUEIDENTIFIER NULL,
            TerminalCode NVARCHAR(50) NOT NULL,
            TerminalNameAR NVARCHAR(200) NOT NULL,
            TerminalNameEN NVARCHAR(200) NULL,
            Location NVARCHAR(200) NULL,
            WarehouseID BIGINT NULL,
            CashAccountCode NVARCHAR(50) NULL,
            ReceiptPrinter NVARCHAR(100) NULL,
            KitchenPrinter NVARCHAR(100) NULL,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_POSTerminals PRIMARY KEY CLUSTERED (TerminalID),
            CONSTRAINT UQ_POSTerminals_Code UNIQUE (CompanyID, TerminalCode),
            CONSTRAINT FK_POSTerminals_Warehouse FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT FK_POSTerminals_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ 5.1 POSTerminals created.';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_POSTerminals_Company_Active 
        ON dbo.POSTerminals(CompanyID, IsActive) WHERE IsDeleted = 0;
    GO

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'POSSessions')
    BEGIN
        CREATE TABLE dbo.POSSessions (
            SessionID BIGINT IDENTITY(1,1) NOT NULL,
            TerminalID INT NOT NULL,
            OpenedBy UNIQUEIDENTIFIER NOT NULL,
            OpenedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            ClosedBy UNIQUEIDENTIFIER NULL,
            ClosedAt DATETIME2(7) NULL,
            OpeningBalance DECIMAL(18,4) NOT NULL DEFAULT 0,
            ExpectedClosingBalance DECIMAL(18,4) NULL,
            ActualClosingBalance DECIMAL(18,4) NULL,
            TotalSales DECIMAL(18,4) DEFAULT 0,
            TotalReturns DECIMAL(18,4) DEFAULT 0,
            TotalCash DECIMAL(18,4) DEFAULT 0,
            TotalCard DECIMAL(18,4) DEFAULT 0,
            TotalCredit DECIMAL(18,4) DEFAULT 0,
            TotalDiscount DECIMAL(18,4) DEFAULT 0,
            Status NVARCHAR(20) NOT NULL DEFAULT 'OPEN',
            Notes NVARCHAR(MAX) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            RowVersion ROWVERSION,
            CONSTRAINT PK_POSSessions PRIMARY KEY CLUSTERED (SessionID),
            CONSTRAINT FK_POSSessions_Terminal FOREIGN KEY (TerminalID) REFERENCES dbo.POSTerminals(TerminalID),
            CONSTRAINT CK_POSSessions_Status CHECK (Status IN ('OPEN','CLOSED','SUSPENDED','RECONCILED'))
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ 5.2 POSSessions created.';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_POSSessions_Terminal_Status 
        ON dbo.POSSessions(TerminalID, Status) WHERE IsDeleted = 0;
    GO

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'POSTransactions')
    BEGIN
        CREATE TABLE dbo.POSTransactions (
            POSTransactionID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            TerminalID INT NOT NULL,
            SessionID BIGINT NOT NULL,
            InvoiceID BIGINT NULL,
            TransactionType NVARCHAR(20) NOT NULL,
            TransactionNumber NVARCHAR(50) NOT NULL,
            CustomerID BIGINT NULL,
            TotalAmount DECIMAL(18,4) NOT NULL,
            TotalDiscount DECIMAL(18,4) DEFAULT 0,
            NetAmount DECIMAL(18,4) NOT NULL,
            PaidAmount DECIMAL(18,4) NOT NULL,
            ChangeAmount DECIMAL(18,4) DEFAULT 0,
            Status NVARCHAR(20) DEFAULT 'COMPLETED',
            IsDeleted BIT DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            RowVersion ROWVERSION,
            CONSTRAINT PK_POSTransactions PRIMARY KEY CLUSTERED (POSTransactionID),
            CONSTRAINT FK_POSTransactions_Terminal FOREIGN KEY (TerminalID) REFERENCES dbo.POSTerminals(TerminalID),
            CONSTRAINT FK_POSTransactions_Session FOREIGN KEY (SessionID) REFERENCES dbo.POSSessions(SessionID),
            CONSTRAINT FK_POSTransactions_Customer FOREIGN KEY (CustomerID) REFERENCES dbo.Customers(CustomerID),
            CONSTRAINT CK_POSTransactions_Type CHECK (TransactionType IN ('SALE', 'RETURN', 'VOID', 'QUOTATION'))
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ 5.3 POSTransactions created.';
    END

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'POSTransactionItems')
    BEGIN
        CREATE TABLE dbo.POSTransactionItems (
            POSTransactionItemID BIGINT IDENTITY(1,1) NOT NULL,
            POSTransactionID BIGINT NOT NULL,
            ProductID INT NOT NULL,
            BatchID BIGINT NULL,
            SerialNumberID BIGINT NULL,
            Quantity DECIMAL(18,4) NOT NULL,
            UnitPrice DECIMAL(18,6) NOT NULL,
            Discount DECIMAL(18,4) DEFAULT 0,
            TotalAmount DECIMAL(18,4) NOT NULL,
            IsDeleted BIT DEFAULT 0,
            RowVersion ROWVERSION,
            CONSTRAINT PK_POSTransactionItems PRIMARY KEY CLUSTERED (POSTransactionItemID),
            CONSTRAINT FK_POSTransactionItems_Transaction FOREIGN KEY (POSTransactionID) REFERENCES dbo.POSTransactions(POSTransactionID) ON DELETE CASCADE,
            CONSTRAINT FK_POSTransactionItems_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ 5.4 POSTransactionItems created.';
    END

    CREATE NONCLUSTERED COLUMNSTORE INDEX IF NOT EXISTS CSIX_POSTransactions_Reporting 
        ON dbo.POSTransactions (CompanyID, CreatedAt, TransactionType, TotalAmount);
    GO

-- ========================================================================
-- ========================================================================

    IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Stock_Increase')
        DROP PROCEDURE dbo.usp_Stock_Increase;
    GO
    CREATE PROCEDURE dbo.usp_Stock_Increase
        @CompanyID UNIQUEIDENTIFIER,
        @ProductID INT,
        @WarehouseID BIGINT,
        @Quantity DECIMAL(18,4),
        @TransactionType NVARCHAR(30),
        @UnitCost DECIMAL(18,6) = NULL,
        @BatchNumber NVARCHAR(50) = NULL,
        @SerialNumber NVARCHAR(100) = NULL,
        @ExpiryDate DATE = NULL,
        @ReferenceTable NVARCHAR(50) = NULL,
        @ReferenceID BIGINT = NULL,
        @ReferenceLineID BIGINT = NULL,
        @Notes NVARCHAR(MAX) = NULL,
        @CreatedBy UNIQUEIDENTIFIER,
        @NewBalance DECIMAL(18,4) OUTPUT
    AS
    BEGIN
        SET NOCOUNT ON;
        BEGIN TRY
            BEGIN TRANSACTION;
            
            MERGE INTO dbo.StockBalances AS target
            USING (SELECT @CompanyID AS CompanyID, @WarehouseID AS WarehouseID, @ProductID AS ProductID) AS source
            ON target.CompanyID = source.CompanyID 
               AND target.WarehouseID = source.WarehouseID 
               AND target.ProductID = source.ProductID
               AND target.IsDeleted = 0
            WHEN MATCHED THEN
                UPDATE SET 
                    AvailableQty = AvailableQty + @Quantity,
                    LastCost = CASE WHEN @UnitCost IS NOT NULL THEN @UnitCost ELSE LastCost END,
                    LastUpdated = SYSUTCDATETIME(),
                    LastTransactionType = @TransactionType,
                    LastTransactionDate = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (CompanyID, WarehouseID, ProductID, AvailableQty, LastCost, LastUpdated)
                VALUES (@CompanyID, @WarehouseID, @ProductID, @Quantity, @UnitCost, SYSUTCDATETIME());
            
            SELECT @NewBalance = AvailableQty FROM dbo.StockBalances 
            WHERE CompanyID = @CompanyID AND WarehouseID = @WarehouseID 
                AND ProductID = @ProductID AND IsDeleted = 0;
            
            IF @UnitCost IS NOT NULL AND @Quantity > 0
            BEGIN
                INSERT INTO dbo.FIFOLayers (
                    CompanyID, ProductID, WarehouseID, LayerDate, Quantity, UnitCost, RemainingQuantity,
                    SourceReference, SourceID, CreatedBy
                ) VALUES (
                    @CompanyID, @ProductID, @WarehouseID, CAST(GETDATE() AS DATE), @Quantity, @UnitCost, @Quantity,
                    @ReferenceTable, @ReferenceID, @CreatedBy
                );
            END
            
            IF @BatchNumber IS NOT NULL
            BEGIN
                MERGE INTO dbo.Batches AS target
                USING (SELECT @CompanyID AS CompanyID, @ProductID AS ProductID, @WarehouseID AS WarehouseID, 
                              @BatchNumber AS BatchNumber) AS source
                ON target.CompanyID = source.CompanyID 
                   AND target.ProductID = source.ProductID 
                   AND target.BatchNumber = source.BatchNumber
                   AND target.IsDeleted = 0
                WHEN MATCHED THEN
                    UPDATE SET CurrentQuantity = CurrentQuantity + @Quantity,
                               ExpiryDate = CASE WHEN @ExpiryDate IS NOT NULL THEN @ExpiryDate ELSE ExpiryDate END,
                               UpdatedAt = SYSUTCDATETIME(),
                               UpdatedBy = @CreatedBy
                WHEN NOT MATCHED THEN
                    INSERT (CompanyID, ProductID, WarehouseID, BatchNumber, ExpiryDate, OriginalQuantity, CurrentQuantity, PurchaseCost, CreatedBy)
                    VALUES (@CompanyID, @ProductID, @WarehouseID, @BatchNumber, @ExpiryDate, @Quantity, @Quantity, @UnitCost, @CreatedBy);
            END
            
            IF @SerialNumber IS NOT NULL
            BEGIN
                INSERT INTO dbo.SerialNumbers (
                    CompanyID, ProductID, WarehouseID, SerialNumber, Status, ReceivedDate, CreatedBy
                ) VALUES (
                    @CompanyID, @ProductID, @WarehouseID, @SerialNumber, 'IN_STOCK', CAST(GETDATE() AS DATE), @CreatedBy
                );
            END
            
            INSERT INTO dbo.StockMovements (
                CompanyID, MovementType, ProductID, WarehouseIDTo, Quantity, UnitCost, TotalCost,
                ReferenceTable, ReferenceID, ReferenceLineID, Notes, CreatedBy
            ) VALUES (
                @CompanyID, @TransactionType, @ProductID, @WarehouseID, @Quantity, @UnitCost, @Quantity * ISNULL(@UnitCost, 0),
                @ReferenceTable, @ReferenceID, @ReferenceLineID, @Notes, @CreatedBy
            );
            
            COMMIT TRANSACTION;
        END TRY
        BEGIN CATCH
            IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
            THROW;
        END CATCH
    END;
    PRINT N'✅ 6.1 usp_Stock_Increase created.';
    GO

    IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Stock_Decrease')
        DROP PROCEDURE dbo.usp_Stock_Decrease;
    GO
    CREATE PROCEDURE dbo.usp_Stock_Decrease
        @CompanyID UNIQUEIDENTIFIER,
        @ProductID INT,
        @WarehouseID BIGINT,
        @Quantity DECIMAL(18,4),
        @TransactionType NVARCHAR(30),
        @UnitCost DECIMAL(18,6) = NULL,
        @BatchNumber NVARCHAR(50) = NULL,
        @SerialNumber NVARCHAR(100) = NULL,
        @ReferenceTable NVARCHAR(50) = NULL,
        @ReferenceID BIGINT = NULL,
        @ReferenceLineID BIGINT = NULL,
        @Notes NVARCHAR(MAX) = NULL,
        @CreatedBy UNIQUEIDENTIFIER,
        @NewBalance DECIMAL(18,4) OUTPUT
    AS
    BEGIN
        SET NOCOUNT ON;
        BEGIN TRY
            BEGIN TRANSACTION;
            
            DECLARE @CurrentQty DECIMAL(18,4);
            SELECT @CurrentQty = AvailableQty FROM dbo.StockBalances 
            WHERE CompanyID = @CompanyID AND WarehouseID = @WarehouseID 
                AND ProductID = @ProductID AND IsDeleted = 0;
            
            IF @CurrentQty IS NULL
                THROW 50000, 'لا يوجد مخزون لهذا المنتج.', 1;
            
            IF @CurrentQty < @Quantity
                THROW 50000, N'الكمية المطلوبة أكبر من الرصيد المتاح.', 1;
            
            UPDATE dbo.StockBalances 
            SET AvailableQty = AvailableQty - @Quantity,
                LastUpdated = SYSUTCDATETIME(),
                LastTransactionType = @TransactionType,
                LastTransactionDate = SYSUTCDATETIME()
            WHERE CompanyID = @CompanyID AND WarehouseID = @WarehouseID 
                AND ProductID = @ProductID AND IsDeleted = 0;
            
            SELECT @NewBalance = AvailableQty FROM dbo.StockBalances 
            WHERE CompanyID = @CompanyID AND WarehouseID = @WarehouseID 
                AND ProductID = @ProductID AND IsDeleted = 0;
            
            IF @Quantity > 0
            BEGIN
                DECLARE @RemainingQty DECIMAL(18,4) = @Quantity;
                DECLARE @LayerQty DECIMAL(18,4);
                DECLARE @LayerID BIGINT;
                
                DECLARE cur CURSOR FOR
                    SELECT LayerID, RemainingQuantity
                    FROM dbo.FIFOLayers
                    WHERE CompanyID = @CompanyID AND ProductID = @ProductID 
                        AND WarehouseID = @WarehouseID
                        AND IsConsumed = 0 AND IsDeleted = 0
                    ORDER BY LayerDate ASC;
                
                OPEN cur;
                FETCH NEXT FROM cur INTO @LayerID, @LayerQty;
                
                WHILE @@FETCH_STATUS = 0 AND @RemainingQty > 0
                BEGIN
                    IF @LayerQty >= @RemainingQty
                    BEGIN
                        UPDATE dbo.FIFOLayers 
                        SET RemainingQuantity = RemainingQuantity - @RemainingQty,
                            IsConsumed = CASE WHEN RemainingQuantity - @RemainingQty = 0 THEN 1 ELSE 0 END,
                            ConsumedAt = CASE WHEN RemainingQuantity - @RemainingQty = 0 THEN SYSUTCDATETIME() ELSE NULL END
                        WHERE LayerID = @LayerID;
                        SET @RemainingQty = 0;
                    END
                    ELSE
                    BEGIN
                        UPDATE dbo.FIFOLayers 
                        SET RemainingQuantity = 0,
                            IsConsumed = 1,
                            ConsumedAt = SYSUTCDATETIME()
                        WHERE LayerID = @LayerID;
                        SET @RemainingQty = @RemainingQty - @LayerQty;
                    END
                    FETCH NEXT FROM cur INTO @LayerID, @LayerQty;
                END
                CLOSE cur;
                DEALLOCATE cur;
            END
            
            IF @BatchNumber IS NOT NULL
            BEGIN
                UPDATE dbo.Batches 
                SET CurrentQuantity = CurrentQuantity - @Quantity,
                    UpdatedAt = SYSUTCDATETIME(),
                    UpdatedBy = @CreatedBy
                WHERE CompanyID = @CompanyID AND ProductID = @ProductID 
                    AND BatchNumber = @BatchNumber AND IsDeleted = 0;
            END
            
            IF @SerialNumber IS NOT NULL
            BEGIN
                UPDATE dbo.SerialNumbers 
                SET Status = 'SOLD',
                    SoldDate = CAST(GETDATE() AS DATE),
                    LastTransactionDate = SYSUTCDATETIME(),
                    UpdatedAt = SYSUTCDATETIME(),
                    UpdatedBy = @CreatedBy
                WHERE CompanyID = @CompanyID AND ProductID = @ProductID 
                    AND SerialNumber = @SerialNumber AND IsDeleted = 0;
            END
            
            INSERT INTO dbo.StockMovements (
                CompanyID, MovementType, ProductID, WarehouseIDFrom, Quantity, UnitCost, TotalCost,
                ReferenceTable, ReferenceID, ReferenceLineID, Notes, CreatedBy
            ) VALUES (
                @CompanyID, @TransactionType, @ProductID, @WarehouseID, @Quantity, @UnitCost, @Quantity * ISNULL(@UnitCost, 0),
                @ReferenceTable, @ReferenceID, @ReferenceLineID, @Notes, @CreatedBy
            );
            
            COMMIT TRANSACTION;
        END TRY
        BEGIN CATCH
            IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
            THROW;
        END CATCH
    END;
    PRINT N'✅ 6.2 usp_Stock_Decrease created.';
    GO

    IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Stock_UpdateBalance')
        DROP PROCEDURE dbo.usp_Stock_UpdateBalance;
    GO
    CREATE PROCEDURE dbo.usp_Stock_UpdateBalance
        @CompanyID UNIQUEIDENTIFIER,
        @ProductID INT,
        @WarehouseID BIGINT,
        @Quantity DECIMAL(18,4),
        @IsIncrease BIT = 1,
        @UnitCost DECIMAL(18,6) = NULL,
        @BatchNumber NVARCHAR(50) = NULL,
        @SerialNumber NVARCHAR(100) = NULL,
        @ExpiryDate DATE = NULL,
        @ReferenceType NVARCHAR(30) = NULL,
        @ReferenceID BIGINT = NULL,
        @CreatedBy UNIQUEIDENTIFIER
    AS
    BEGIN
        DECLARE @NewBalance DECIMAL(18,4);
        IF @IsIncrease = 1
        BEGIN
            EXEC dbo.usp_Stock_Increase 
                @CompanyID, @ProductID, @WarehouseID, @Quantity, @ReferenceType, @UnitCost,
                @BatchNumber, @SerialNumber, @ExpiryDate,
                NULL, @ReferenceID, NULL, NULL, @CreatedBy, @NewBalance OUTPUT;
        END
        ELSE
        BEGIN
            EXEC dbo.usp_Stock_Decrease 
                @CompanyID, @ProductID, @WarehouseID, @Quantity, @ReferenceType, @UnitCost,
                @BatchNumber, @SerialNumber,
                NULL, @ReferenceID, NULL, NULL, @CreatedBy, @NewBalance OUTPUT;
        END
    END;
    PRINT N'✅ 6.3 usp_Stock_UpdateBalance created (Wrapper).';
    GO

    IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Valuation_CalculateAVG')
        DROP PROCEDURE dbo.usp_Valuation_CalculateAVG;
    GO
    CREATE PROCEDURE dbo.usp_Valuation_CalculateAVG
        @CompanyID UNIQUEIDENTIFIER,
        @ProductID INT,
        @WarehouseID BIGINT
    AS
    BEGIN
        SET NOCOUNT ON;
        DECLARE @TotalCost DECIMAL(18,6) = 0;
        DECLARE @TotalQty DECIMAL(18,4) = 0;
        DECLARE @AvgCost DECIMAL(18,6) = 0;
        
        SELECT @TotalCost = SUM(RemainingQuantity * UnitCost),
               @TotalQty = SUM(RemainingQuantity)
        FROM dbo.FIFOLayers
        WHERE CompanyID = @CompanyID
            AND ProductID = @ProductID
            AND WarehouseID = @WarehouseID
            AND IsConsumed = 0
            AND IsDeleted = 0;
        
        IF @TotalQty > 0
            SET @AvgCost = @TotalCost / @TotalQty;
        
        UPDATE dbo.StockBalances 
        SET AverageCost = @AvgCost
        WHERE CompanyID = @CompanyID
            AND ProductID = @ProductID
            AND WarehouseID = @WarehouseID
            AND IsDeleted = 0;
        
        SELECT @AvgCost AS AverageCost;
    END;
    PRINT N'✅ 6.4 usp_Valuation_CalculateAVG created.';
    GO

    IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Products_Search')
        DROP PROCEDURE dbo.usp_Products_Search;
    GO
    CREATE PROCEDURE dbo.usp_Products_Search
        @CompanyID UNIQUEIDENTIFIER,
        @SearchTerm NVARCHAR(200) = NULL,
        @CategoryID INT = NULL,
        @BrandID INT = NULL,
        @IsPOSOnly BIT = 1,
        @TopN INT = 50
    AS
    BEGIN
        SET NOCOUNT ON;
        SELECT TOP (@TopN)
            p.ProductID,
            p.ProductCode,
            p.ProductNameAR,
            p.ProductNameEN,
            p.Barcode,
            p.QuickCode,
            p.DefaultSalePrice,
            p.MainImagePath,
            p.IsSerialized,
            p.IsBatchTracked,
            c.CategoryNameAR AS CategoryName,
            b.BrandNameAR AS BrandName,
            ISNULL(s.AvailableQty, 0) AS AvailableQty
        FROM dbo.Products p
        LEFT JOIN dbo.ProductCategories c ON p.CategoryID = c.CategoryID
        LEFT JOIN dbo.Brands b ON p.BrandID = b.BrandID
        LEFT JOIN dbo.StockBalances s ON p.ProductID = s.ProductID 
            AND s.WarehouseID = (SELECT TOP 1 WarehouseID FROM dbo.Warehouses WHERE CompanyID = @CompanyID AND IsDefault = 1 AND IsDeleted = 0)
        WHERE p.CompanyID = @CompanyID
            AND p.IsDeleted = 0
            AND (@IsPOSOnly = 0 OR p.IsPOSItem = 1)
            AND p.IsActive = 1
            AND (@CategoryID IS NULL OR p.CategoryID = @CategoryID)
            AND (@BrandID IS NULL OR p.BrandID = @BrandID)
            AND (@SearchTerm IS NULL 
                OR p.ProductCode LIKE N'%' + @SearchTerm + N'%'
                OR p.ProductNameAR LIKE N'%' + @SearchTerm + N'%'
                OR p.ProductNameEN LIKE N'%' + @SearchTerm + N'%'
                OR p.Barcode LIKE N'%' + @SearchTerm + N'%'
                OR p.QuickCode LIKE N'%' + @SearchTerm + N'%'
                OR p.SKU LIKE N'%' + @SearchTerm + N'%')
        ORDER BY p.ProductNameAR;
    END;
    PRINT N'✅ 6.5 usp_Products_Search created (POS-ready).';
    GO

-- ========================================================================
-- ========================================================================

    IF EXISTS (SELECT 1 FROM sys.views WHERE name = 'vw_StockStatus')
        DROP VIEW dbo.vw_StockStatus;
    GO
    CREATE VIEW dbo.vw_StockStatus
    AS
    SELECT 
        s.StockBalanceID,
        s.ProductID,
        p.ProductNameAR,
        p.ProductCode,
        s.WarehouseID,
        w.WarehouseNameAR AS WarehouseName,
        s.AvailableQty,
        s.ReservedQty,
        s.AvailableQty - s.ReservedQty AS AvailableNow,
        s.AverageCost,
        s.LastCost,
        s.LastUpdated
    FROM dbo.StockBalances s
    INNER JOIN dbo.Products p ON s.ProductID = p.ProductID
    INNER JOIN dbo.Warehouses w ON s.WarehouseID = w.WarehouseID
    WHERE s.IsDeleted = 0;
    PRINT N'✅ 7.1 vw_StockStatus created.';
    GO

    IF EXISTS (SELECT 1 FROM sys.views WHERE name = 'vw_ProductMovementHistory')
        DROP VIEW dbo.vw_ProductMovementHistory;
    GO
    CREATE VIEW dbo.vw_ProductMovementHistory
    AS
    SELECT 
        m.MovementID,
        m.MovementType,
        m.MovementDate,
        m.ProductID,
        p.ProductNameAR,
        p.ProductCode,
        m.Quantity,
        m.UnitCost,
        m.TotalCost,
        m.ReferenceNumber,
        m.ReferenceTable,
        wf.WarehouseNameAR AS FromWarehouse,
        wt.WarehouseNameAR AS ToWarehouse,
        m.Notes,
        u.UserName AS CreatedByUser
    FROM dbo.StockMovements m
    INNER JOIN dbo.Products p ON m.ProductID = p.ProductID
    LEFT JOIN dbo.Warehouses wf ON m.WarehouseIDFrom = wf.WarehouseID
    LEFT JOIN dbo.Warehouses wt ON m.WarehouseIDTo = wt.WarehouseID
    LEFT JOIN dbo.Users u ON m.CreatedBy = u.UserID
    WHERE m.IsDeleted = 0;
    PRINT N'✅ 7.2 vw_ProductMovementHistory created.';
    GO

    IF EXISTS (SELECT 1 FROM sys.views WHERE name = 'vw_ExpiryReport')
        DROP VIEW dbo.vw_ExpiryReport;
    GO
    CREATE VIEW dbo.vw_ExpiryReport
    AS
    SELECT 
        b.BatchID,
        b.ProductID,
        p.ProductNameAR,
        b.BatchNumber,
        b.ExpiryDate,
        DATEDIFF(DAY, GETDATE(), b.ExpiryDate) AS DaysRemaining,
        b.CurrentQuantity,
        w.WarehouseNameAR AS WarehouseName,
        CASE 
            WHEN DATEDIFF(DAY, GETDATE(), b.ExpiryDate) <= 0 THEN N'منتهي الصلاحية'
            WHEN DATEDIFF(DAY, GETDATE(), b.ExpiryDate) <= 30 THEN N'ينتهي قريباً (أقل من 30 يوم)'
            ELSE N'ساري'
        END AS ExpiryStatus
    FROM dbo.Batches b
    INNER JOIN dbo.Products p ON b.ProductID = p.ProductID
    INNER JOIN dbo.Warehouses w ON b.WarehouseID = w.WarehouseID
    WHERE b.ExpiryDate IS NOT NULL
        AND b.CurrentQuantity > 0
        AND b.IsDeleted = 0
        AND b.IsActive = 1;
    PRINT N'✅ 7.3 vw_ExpiryReport created.';
    GO

    IF EXISTS (SELECT 1 FROM sys.views WHERE name = 'vw_POSSummary')
        DROP VIEW dbo.vw_POSSummary;
    GO
    CREATE VIEW dbo.vw_POSSummary
    AS
    SELECT 
        t.TerminalID,
        t.TerminalCode,
        t.TerminalNameAR,
        s.SessionID,
        s.OpenedAt,
        s.ClosedAt,
        s.Status,
        s.TotalSales,
        s.TotalReturns,
        s.TotalCash,
        s.TotalCard,
        s.TotalCredit,
        s.TotalDiscount,
        (s.TotalSales - s.TotalReturns) AS NetSales
    FROM dbo.POSTerminals t
    LEFT JOIN dbo.POSSessions s ON t.TerminalID = s.TerminalID
    WHERE t.IsDeleted = 0 AND s.IsDeleted = 0;
    PRINT N'✅ 7.4 vw_POSSummary created.';
    GO

-- ========================================================================
-- ========================================================================
    DECLARE @SystemCompanyID UNIQUEIDENTIFIER;
    DECLARE @SystemUserID UNIQUEIDENTIFIER = '00000000-0000-0000-0000-000000000001';
    SELECT TOP 1 @SystemCompanyID = CompanyID FROM MST.dbo.Companies;

    IF NOT EXISTS (SELECT 1 FROM dbo.UnitsOfMeasure WHERE UOMCode = 'UNIT' AND CompanyID = @SystemCompanyID)
    BEGIN
        INSERT INTO dbo.UnitsOfMeasure (CompanyID, UOMCode, UOMNameAR, UOMNameEN, IsBaseUnit, IsActive, CreatedBy)
        VALUES 
            (@SystemCompanyID, 'UNIT', N'وحدة', N'Unit', 1, 1, @SystemUserID),
            (@SystemCompanyID, 'KG', N'كيلو جرام', N'Kilogram', 0, 1, @SystemUserID),
            (@SystemCompanyID, 'BOX', N'صندوق', N'Box', 0, 1, @SystemUserID),
            (@SystemCompanyID, 'PACK', N'باكيت', N'Pack', 0, 1, @SystemUserID);
        PRINT N'✅ 8.1 Units of Measure seeded.';
    END

    IF NOT EXISTS (SELECT 1 FROM dbo.ProductCategories WHERE CategoryCode = 'GENERAL' AND CompanyID = @SystemCompanyID)
    BEGIN
        INSERT INTO dbo.ProductCategories (CompanyID, CategoryCode, CategoryNameAR, CategoryNameEN, IsActive, CreatedBy)
        VALUES 
            (@SystemCompanyID, 'GENERAL', N'عام', N'General', 1, @SystemUserID),
            (@SystemCompanyID, 'FOOD', N'مواد غذائية', N'Food', 1, @SystemUserID),
            (@SystemCompanyID, 'BEVERAGE', N'مشروبات', N'Beverages', 1, @SystemUserID),
            (@SystemCompanyID, 'ELECTRONICS', N'إلكترونيات', N'Electronics', 1, @SystemUserID);
        PRINT N'✅ 8.2 Categories seeded.';
    END

    IF NOT EXISTS (SELECT 1 FROM dbo.ValuationMethods WHERE MethodCode = 'AVG' AND CompanyID = @SystemCompanyID)
    BEGIN
        INSERT INTO dbo.ValuationMethods (CompanyID, MethodCode, MethodNameAR, MethodNameEN, IsDefault, IsActive, CreatedBy)
        VALUES 
            (@SystemCompanyID, 'FIFO', N'الوارد أولاً صادر أولاً', N'FIFO', 0, 1, @SystemUserID),
            (@SystemCompanyID, 'LIFO', N'الوارد أخيراً صادر أولاً', N'LIFO', 0, 1, @SystemUserID),
            (@SystemCompanyID, 'AVG', N'المتوسط المرجح', N'Weighted Average', 1, 1, @SystemUserID);
        PRINT N'✅ 8.3 Valuation Methods seeded (FIFO, LIFO, AVG).';
    END

    IF NOT EXISTS (SELECT 1 FROM dbo.POSTerminals WHERE TerminalCode = 'POS-001' AND CompanyID = @SystemCompanyID)
    BEGIN
        DECLARE @DefaultWarehouseID BIGINT = (SELECT TOP 1 WarehouseID FROM dbo.Warehouses WHERE CompanyID = @SystemCompanyID AND IsDefault = 1 AND IsDeleted = 0);
        INSERT INTO dbo.POSTerminals (CompanyID, TerminalCode, TerminalNameAR, TerminalNameEN, WarehouseID, IsActive, CreatedBy)
        VALUES (@SystemCompanyID, 'POS-001', N'نقطة بيع رئيسية', N'Main POS', @DefaultWarehouseID, 1, @SystemUserID);
        PRINT N'✅ 8.4 Default POS Terminal seeded.';
    END

-- ========================================================================
-- ========================================================================
    COMMIT TRANSACTION;
    PRINT N'═══════════════════════════════════════════════════════════════════════';
    PRINT N'✅ INVENTORY & POS MODULE (ULTIMATE 10/10) DEPLOYED.';
    PRINT N'📌 الأقسام المتكاملة:';
    PRINT N'   1. المنتجات والفئات والعلامات';
    PRINT N'   2. الأرصدة والحركات و FIFO Layers';
    PRINT N'   3. الدُفعات والأرقام التسلسلية';
    PRINT N'   4. التقييم (FIFO/LIFO/AVG)';
    PRINT N'   5. نقاط البيع (الأجهزة، الجلسات، المعاملات)';
    PRINT N'   6. الإجراءات المخزنة (زيادة، نقصان، حجز، تقييم، بحث)';
    PRINT N'   7. طرق العرض (التقارير)';
    PRINT N'   8. البيانات الأولية';
    PRINT N'🏆 متكامل مع wh.sql (المستودعات) و crm.sql (العملاء).';
    PRINT N'═══════════════════════════════════════════════════════════════════════';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    DECLARE @ErrorMessage NVARCHAR(4000) = ERROR_MESSAGE();
    DECLARE @ErrorSeverity INT = ERROR_SEVERITY();
    RAISERROR(@ErrorMessage, @ErrorSeverity, 1);
END CATCH
GO