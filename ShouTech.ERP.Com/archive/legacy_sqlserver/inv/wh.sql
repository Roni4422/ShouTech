-- ========================================================================
-- FILE: dba/inv/wh_ultimate_v2.sql
-- PROJECT: SHOUTECH ERP V10 - ULTIMATE WAREHOUSE MANAGEMENT (10.10.0)
-- DESCRIPTION: 
-- ========================================================================
-- ========================================================================

USE [$(DbFileName)];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

PRINT N'═══════════════════════════════════════════════════════════════════════';
PRINT N'SHOUTECH ERP V10 – ULTIMATE WAREHOUSE MANAGEMENT (10.10.0)';
PRINT N'═══════════════════════════════════════════════════════════════════════';
PRINT N'📌 WMS: Zones, Racks, Bins, Batches, ZATCA, Multi-Currency';
PRINT N'═══════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

-- ========================================================================
-- ========================================================================

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WarehouseTypes')
    BEGIN
        CREATE TABLE dbo.WarehouseTypes (
            WarehouseTypeID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            TypeCode NVARCHAR(50) NOT NULL,
            TypeNameAR NVARCHAR(100) NOT NULL,
            TypeNameEN NVARCHAR(100) NOT NULL,
            Description NVARCHAR(300) NULL,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_WarehouseTypes PRIMARY KEY CLUSTERED (WarehouseTypeID),
            CONSTRAINT UQ_WarehouseTypes_Code UNIQUE (CompanyID, TypeCode),
            CONSTRAINT FK_WarehouseTypes_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ [1.1] WarehouseTypes created.';
    END

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WarehouseZones')
    BEGIN
        CREATE TABLE dbo.WarehouseZones (
            ZoneID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            WarehouseID BIGINT NOT NULL,
            ZoneCode NVARCHAR(50) NOT NULL,
            ZoneNameAR NVARCHAR(200) NOT NULL,
            ZoneNameEN NVARCHAR(200) NULL,
            ZoneType NVARCHAR(30) NOT NULL, -- RECEIVING, STORAGE, PICKING, PACKING, SHIPPING, QUARANTINE, DAMAGED
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
            CONSTRAINT PK_WarehouseZones PRIMARY KEY CLUSTERED (ZoneID),
            CONSTRAINT UQ_WarehouseZones_Code UNIQUE (WarehouseID, ZoneCode),
            CONSTRAINT FK_WarehouseZones_Warehouse FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID) ON DELETE CASCADE,
            CONSTRAINT FK_WarehouseZones_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID),
            CONSTRAINT CK_WarehouseZones_Type CHECK (ZoneType IN ('RECEIVING', 'STORAGE', 'PICKING', 'PACKING', 'SHIPPING', 'QUARANTINE', 'DAMAGED'))
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ [1.2] WarehouseZones created (Advanced zone management).';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_WarehouseZones_Warehouse 
        ON dbo.WarehouseZones(WarehouseID, IsActive) WHERE IsDeleted = 0;
    GO

-- ========================================================================
-- ========================================================================

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Warehouses')
    BEGIN
        CREATE TABLE dbo.Warehouses (
            WarehouseID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            BranchID UNIQUEIDENTIFIER NULL,
            WarehouseTypeID INT NOT NULL,
            WarehouseCode NVARCHAR(50) NOT NULL,
            WarehouseNameAR NVARCHAR(200) NOT NULL,
            WarehouseNameEN NVARCHAR(200) NULL,
            AddressLine1 NVARCHAR(300) NULL,
            AddressLine2 NVARCHAR(300) NULL,
            City NVARCHAR(100) NULL,
            Region NVARCHAR(100) NULL,
            Country NVARCHAR(100) DEFAULT N'المملكة العربية السعودية',
            PostalCode NVARCHAR(20) NULL,
            Latitude DECIMAL(10,7) NULL,
            Longitude DECIMAL(10,7) NULL,
            Phone NVARCHAR(50) NULL,
            Email NVARCHAR(100) NULL,
            ManagerEmployeeID BIGINT NULL,
            SupervisorEmployeeID BIGINT NULL,
            MaxCapacity DECIMAL(18,3) NULL,
            CurrentOccupancy DECIMAL(18,3) NULL,
            LastOccupancyUpdate DATETIME2(7) NULL,
            IsDefault BIT NOT NULL DEFAULT 0,
            IsConsignment BIT NOT NULL DEFAULT 0,
            AllowNegativeStock BIT NOT NULL DEFAULT 0,
            AutoReserveOnReceipt BIT NOT NULL DEFAULT 1,
            IsActive BIT NOT NULL DEFAULT 1,
            IsFrozen BIT NOT NULL DEFAULT 0,
            FrozenAt DATETIME2(7) NULL,
            FrozenBy UNIQUEIDENTIFIER NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_Warehouses PRIMARY KEY CLUSTERED (WarehouseID),
            CONSTRAINT UQ_Warehouses_Code UNIQUE (CompanyID, WarehouseCode),
            CONSTRAINT FK_Warehouses_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID),
            CONSTRAINT FK_Warehouses_Type FOREIGN KEY (WarehouseTypeID) REFERENCES dbo.WarehouseTypes(WarehouseTypeID)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ [2.1] Warehouses created (Enhanced).';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Warehouses_Company_Active 
        ON dbo.Warehouses(CompanyID, IsActive) INCLUDE (WarehouseCode, WarehouseNameAR, BranchID, IsDefault, IsFrozen) 
        WHERE IsDeleted = 0;

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WarehouseLocations')
    BEGIN
        CREATE TABLE dbo.WarehouseLocations (
            LocationID BIGINT IDENTITY(1,1) NOT NULL,
            WarehouseID BIGINT NOT NULL,
            ZoneID INT NULL,
            LocationCode NVARCHAR(50) NOT NULL,
            LocationName NVARCHAR(200) NULL,
            Aisle NVARCHAR(50) NULL,
            Rack NVARCHAR(50) NULL,
            Shelf NVARCHAR(50) NULL,
            Bin NVARCHAR(50) NULL,
            LocationType NVARCHAR(20) NOT NULL DEFAULT 'STORAGE', -- STORAGE, PICKING, BULK, DAMAGED, QUARANTINE
            MaxWeight DECIMAL(18,3) NULL,
            MaxVolume DECIMAL(18,3) NULL,
            CurrentWeight DECIMAL(18,3) NULL,
            CurrentVolume DECIMAL(18,3) NULL,
            IsActive BIT NOT NULL DEFAULT 1,
            IsBlocked BIT NOT NULL DEFAULT 0,
            BlockReason NVARCHAR(200) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_WarehouseLocations PRIMARY KEY CLUSTERED (LocationID),
            CONSTRAINT UQ_WarehouseLocations_Code UNIQUE (WarehouseID, LocationCode),
            CONSTRAINT FK_WarehouseLocations_Warehouse FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID) ON DELETE CASCADE,
            CONSTRAINT FK_WarehouseLocations_Zone FOREIGN KEY (ZoneID) REFERENCES dbo.WarehouseZones(ZoneID),
            CONSTRAINT CK_WarehouseLocations_Type CHECK (LocationType IN ('STORAGE', 'PICKING', 'BULK', 'DAMAGED', 'QUARANTINE'))
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ [2.2] WarehouseLocations created (Rack/Bin management).';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_WarehouseLocations_Warehouse_Zone 
        ON dbo.WarehouseLocations(WarehouseID, ZoneID) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_WarehouseLocations_Aisle_Rack_Bin 
        ON dbo.WarehouseLocations(Aisle, Rack, Shelf, Bin) WHERE IsDeleted = 0;
    GO

-- ========================================================================
-- ========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WarehouseBatches')
    BEGIN
        CREATE TABLE dbo.WarehouseBatches (
            BatchID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ProductID INT NOT NULL,
            WarehouseID BIGINT NOT NULL,
            LocationID BIGINT NULL,
            BatchNumber NVARCHAR(50) NOT NULL,
            SupplierBatchNumber NVARCHAR(50) NULL,
            ManufacturingDate DATE NULL,
            ExpiryDate DATE NULL,
            ReceivedDate DATE NOT NULL DEFAULT CAST(GETDATE() AS DATE),
            OriginalQuantity DECIMAL(18,4) NOT NULL,
            CurrentQuantity DECIMAL(18,4) NOT NULL,
            ReservedQuantity DECIMAL(18,4) NOT NULL DEFAULT 0,
            PurchaseCost DECIMAL(18,6) NULL,
            AverageCost DECIMAL(18,6) NULL,
            ZATCAComplianceStatus NVARCHAR(20) NOT NULL DEFAULT 'PENDING',
            TaxNumber NVARCHAR(100) NULL,
            SupplierInvoiceNumber NVARCHAR(50) NULL,
            SupplierInvoiceDate DATE NULL,
            IsActive BIT NOT NULL DEFAULT 1,
            IsFullyConsumed BIT NOT NULL DEFAULT 0,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_WarehouseBatches PRIMARY KEY CLUSTERED (BatchID),
            CONSTRAINT UQ_WarehouseBatches_Number UNIQUE (CompanyID, ProductID, BatchNumber),
            CONSTRAINT FK_WarehouseBatches_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID),
            CONSTRAINT FK_WarehouseBatches_Warehouse FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT FK_WarehouseBatches_Location FOREIGN KEY (LocationID) REFERENCES dbo.WarehouseLocations(LocationID),
            CONSTRAINT CK_WarehouseBatches_ZATCA CHECK (ZATCAComplianceStatus IN ('PENDING', 'VERIFIED', 'REJECTED'))
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ [3] WarehouseBatches created (Batch tracking with ZATCA).';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_WarehouseBatches_Expiry 
        ON dbo.WarehouseBatches(ExpiryDate, ProductID) WHERE IsActive = 1 AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_WarehouseBatches_Location 
        ON dbo.WarehouseBatches(LocationID) WHERE LocationID IS NOT NULL AND IsDeleted = 0;
    GO

-- ========================================================================
-- ========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Stock')
    BEGIN
        CREATE TABLE dbo.Stock (
            StockID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            WarehouseID BIGINT NOT NULL,
            LocationID BIGINT NULL,
            ProductID INT NOT NULL,
            QuantityOnHand DECIMAL(18,3) NOT NULL DEFAULT 0,
            ReservedQuantity DECIMAL(18,3) NOT NULL DEFAULT 0,
            QuantityOnOrder DECIMAL(18,3) NOT NULL DEFAULT 0,
            QuantityAvailable AS (QuantityOnHand - ReservedQuantity) PERSISTED,
            LastTransactionDate DATETIME2(7) NULL,
            LastTransactionType NVARCHAR(20) NULL,
            ReorderPoint DECIMAL(18,3) NOT NULL DEFAULT 0,
            SafetyStock DECIMAL(18,3) NOT NULL DEFAULT 0,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_Stock PRIMARY KEY CLUSTERED (StockID),
            CONSTRAINT UQ_Stock_Product_Warehouse UNIQUE (CompanyID, WarehouseID, LocationID, ProductID),
            CONSTRAINT FK_Stock_Warehouse FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT FK_Stock_Location FOREIGN KEY (LocationID) REFERENCES dbo.WarehouseLocations(LocationID)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ [4] Stock created (Perpetual Inventory base).';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Stock_Product_Warehouse 
        ON dbo.Stock(ProductID, WarehouseID) INCLUDE (QuantityOnHand, ReservedQuantity, ReorderPoint) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Stock_LowStock 
        ON dbo.Stock(QuantityAvailable, ReorderPoint) WHERE QuantityAvailable <= ReorderPoint AND IsDeleted = 0;
    GO

-- ========================================================================
-- ========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'StockTransactions')
    BEGIN
        CREATE TABLE dbo.StockTransactions (
            TransactionID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            StockID BIGINT NOT NULL,
            TransactionType NVARCHAR(30) NOT NULL,
            Quantity DECIMAL(18,3) NOT NULL,
            QuantityBefore DECIMAL(18,3) NOT NULL,
            QuantityAfter DECIMAL(18,3) NOT NULL,
            UnitCost DECIMAL(18,6) NULL,
            ReferenceNumber NVARCHAR(50) NULL,
            ReferenceTable NVARCHAR(50) NULL,
            ReferenceID BIGINT NULL,
            ReferenceLineID BIGINT NULL,
            ZATCAComplianceStatus NVARCHAR(20) NULL DEFAULT 'PENDING',
            Notes NVARCHAR(MAX) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            RowVersion ROWVERSION,
            CONSTRAINT PK_StockTransactions PRIMARY KEY CLUSTERED (TransactionID),
            CONSTRAINT FK_StockTransactions_Stock FOREIGN KEY (StockID) REFERENCES dbo.Stock(StockID) ON DELETE CASCADE,
            CONSTRAINT CK_StockTransactions_Type CHECK (TransactionType IN 
                ('SALE', 'PURCHASE', 'TRANSFER_OUT', 'TRANSFER_IN', 
                 'ADJUSTMENT', 'RETURN_SALE', 'RETURN_PURCHASE', 
                 'RESERVATION', 'RELEASE', 'COUNT_OPEN', 'COUNT_CLOSE', 'LOCATION_MOVE'))
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ [5] StockTransactions created (Audit trail).';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_StockTransactions_Stock 
        ON dbo.StockTransactions(StockID, CreatedAt DESC) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_StockTransactions_Reference 
        ON dbo.StockTransactions(ReferenceTable, ReferenceID) WHERE IsDeleted = 0;
    GO

-- ========================================================================
-- ========================================================================

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WarehouseCounts')
    BEGIN
        CREATE TABLE dbo.WarehouseCounts (
            CountID BIGINT IDENTITY(1,1) NOT NULL,
            WarehouseID BIGINT NOT NULL,
            CountNumber NVARCHAR(50) NOT NULL,
            CountDate DATE NOT NULL DEFAULT CAST(GETDATE() AS DATE),
            CountType NVARCHAR(20) NOT NULL DEFAULT 'FULL',
            CountStatus NVARCHAR(20) NOT NULL DEFAULT 'OPEN',
            TotalItemsExpected INT NOT NULL DEFAULT 0,
            TotalItemsCounted INT NOT NULL DEFAULT 0,
            TotalDifferences INT NOT NULL DEFAULT 0,
            StartedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            CompletedAt DATETIME2(7) NULL,
            CountedByUserID UNIQUEIDENTIFIER NULL,
            SupervisedByUserID UNIQUEIDENTIFIER NULL,
            ZATCAComplianceStatus NVARCHAR(20) NULL DEFAULT 'PENDING',
            Notes NVARCHAR(MAX) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_WarehouseCounts PRIMARY KEY CLUSTERED (CountID),
            CONSTRAINT UQ_WarehouseCounts_Number UNIQUE (WarehouseID, CountNumber),
            CONSTRAINT FK_WarehouseCounts_Warehouse FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID) ON DELETE CASCADE,
            CONSTRAINT CK_WarehouseCounts_Type CHECK (CountType IN ('FULL', 'CYCLE', 'SPOT')),
            CONSTRAINT CK_WarehouseCounts_Status CHECK (CountStatus IN ('OPEN', 'IN_PROGRESS', 'COMPLETED', 'CANCELLED'))
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ [6.1] WarehouseCounts header created.';
    END

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WarehouseCountItems')
    BEGIN
        CREATE TABLE dbo.WarehouseCountItems (
            CountItemID BIGINT IDENTITY(1,1) NOT NULL,
            CountID BIGINT NOT NULL,
            ProductID INT NOT NULL,
            LocationID BIGINT NULL,
            ExpectedQuantity DECIMAL(18,3) NOT NULL DEFAULT 0,
            PhysicalQuantity DECIMAL(18,3) NOT NULL DEFAULT 0,
            Difference AS (PhysicalQuantity - ExpectedQuantity) PERSISTED,
            UnitCost DECIMAL(18,6) NULL,
            BatchID BIGINT NULL,
            Notes NVARCHAR(200) NULL,
            IsCounted BIT NOT NULL DEFAULT 0,
            CountedAt DATETIME2(7) NULL,
            CountedByUserID UNIQUEIDENTIFIER NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_WarehouseCountItems PRIMARY KEY CLUSTERED (CountItemID),
            CONSTRAINT UQ_WarehouseCountItems_Product UNIQUE (CountID, ProductID, LocationID, BatchID),
            CONSTRAINT FK_WarehouseCountItems_Count FOREIGN KEY (CountID) REFERENCES dbo.WarehouseCounts(CountID) ON DELETE CASCADE,
            CONSTRAINT FK_WarehouseCountItems_Location FOREIGN KEY (LocationID) REFERENCES dbo.WarehouseLocations(LocationID),
            CONSTRAINT FK_WarehouseCountItems_Batch FOREIGN KEY (BatchID) REFERENCES dbo.WarehouseBatches(BatchID)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ [6.2] WarehouseCountItems details created.';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_WarehouseCountItems_Count 
        ON dbo.WarehouseCountItems(CountID) WHERE IsDeleted = 0;
    GO

-- ========================================================================
-- ========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WarehouseTransfers')
    BEGIN
        CREATE TABLE dbo.WarehouseTransfers (
            TransferID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            TransferNumber NVARCHAR(50) NOT NULL,
            FromWarehouseID BIGINT NOT NULL,
            ToWarehouseID BIGINT NOT NULL,
            FromLocationID BIGINT NULL,
            ToLocationID BIGINT NULL,
            TransferDate DATE NOT NULL DEFAULT CAST(GETDATE() AS DATE),
            TransferType NVARCHAR(20) NOT NULL DEFAULT 'DIRECT',
            TransferStatus NVARCHAR(20) NOT NULL DEFAULT 'DRAFT',
            ReferenceNumber NVARCHAR(50) NULL,
            ReferenceTable NVARCHAR(50) NULL,
            ReferenceID BIGINT NULL,
            ExpectedArrivalDate DATE NULL,
            ActualArrivalDate DATE NULL,
            DispatchedAt DATETIME2(7) NULL,
            ReceivedAt DATETIME2(7) NULL,
            Carrier NVARCHAR(100) NULL,
            TrackingNumber NVARCHAR(100) NULL,
            ZATCAComplianceStatus NVARCHAR(20) NULL DEFAULT 'PENDING',
            Notes NVARCHAR(MAX) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_WarehouseTransfers PRIMARY KEY CLUSTERED (TransferID),
            CONSTRAINT UQ_WarehouseTransfers_Number UNIQUE (CompanyID, TransferNumber),
            CONSTRAINT FK_WarehouseTransfers_FromWarehouse FOREIGN KEY (FromWarehouseID) REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT FK_WarehouseTransfers_ToWarehouse FOREIGN KEY (ToWarehouseID) REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT FK_WarehouseTransfers_FromLocation FOREIGN KEY (FromLocationID) REFERENCES dbo.WarehouseLocations(LocationID),
            CONSTRAINT FK_WarehouseTransfers_ToLocation FOREIGN KEY (ToLocationID) REFERENCES dbo.WarehouseLocations(LocationID),
            CONSTRAINT CK_WarehouseTransfers_Type CHECK (TransferType IN ('DIRECT', 'TRANSIT', 'SPLIT')),
            CONSTRAINT CK_WarehouseTransfers_Status CHECK (TransferStatus IN ('DRAFT', 'PENDING', 'IN_TRANSIT', 'COMPLETED', 'CANCELLED'))
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ [7] WarehouseTransfers created (Enhanced with Locations & ZATCA).';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_WarehouseTransfers_From 
        ON dbo.WarehouseTransfers(FromWarehouseID, TransferStatus) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_WarehouseTransfers_To 
        ON dbo.WarehouseTransfers(ToWarehouseID, TransferStatus) WHERE IsDeleted = 0;
    GO

-- ========================================================================
-- ========================================================================

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WarehouseStaff')
    BEGIN
        CREATE TABLE dbo.WarehouseStaff (
            StaffID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            EmployeeID BIGINT NOT NULL,           -- FK إلى جدول الموظفين (سيُربط لاحقاً)
            WarehouseID BIGINT NOT NULL,
            JobTitle NVARCHAR(100) NULL,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_WarehouseStaff PRIMARY KEY CLUSTERED (StaffID),
            CONSTRAINT FK_WarehouseStaff_Warehouse FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID) ON DELETE CASCADE,
            CONSTRAINT FK_WarehouseStaff_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ [8.1] WarehouseStaff created.';
    END

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WarehouseCheckInOut')
    BEGIN
        CREATE TABLE dbo.WarehouseCheckInOut (
            CheckID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            StaffID BIGINT NOT NULL,
            WarehouseID BIGINT NOT NULL,
            CheckType NVARCHAR(10) NOT NULL,     -- CHECK_IN, CHECK_OUT
            CheckTime DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            DeviceID NVARCHAR(100) NULL,
            IPAddress NVARCHAR(45) NULL,
            Notes NVARCHAR(200) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            RowVersion ROWVERSION,
            CONSTRAINT PK_WarehouseCheckInOut PRIMARY KEY CLUSTERED (CheckID),
            CONSTRAINT FK_WarehouseCheckInOut_Staff FOREIGN KEY (StaffID) REFERENCES dbo.WarehouseStaff(StaffID) ON DELETE CASCADE,
            CONSTRAINT FK_WarehouseCheckInOut_Warehouse FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT FK_WarehouseCheckInOut_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID),
            CONSTRAINT CK_WarehouseCheckInOut_Type CHECK (CheckType IN ('CHECK_IN', 'CHECK_OUT'))
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ [8.2] WarehouseCheckInOut created (Staff tracking).';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_WarehouseCheckInOut_Staff 
        ON dbo.WarehouseCheckInOut(StaffID, CheckTime DESC) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_WarehouseCheckInOut_Warehouse 
        ON dbo.WarehouseCheckInOut(WarehouseID, CheckTime DESC) WHERE IsDeleted = 0;
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
        @LocationID BIGINT = NULL,
        @Quantity DECIMAL(18,3),
        @TransactionType NVARCHAR(30),
        @UnitCost DECIMAL(18,6) = NULL,
        @BatchNumber NVARCHAR(50) = NULL,
        @ExpiryDate DATE = NULL,
        @ZATCAStatus NVARCHAR(20) = 'PENDING',
        @ReferenceTable NVARCHAR(50) = NULL,
        @ReferenceID BIGINT = NULL,
        @ReferenceLineID BIGINT = NULL,
        @Notes NVARCHAR(MAX) = NULL,
        @CreatedBy UNIQUEIDENTIFIER,
        @NewBalance DECIMAL(18,3) OUTPUT
    AS
    BEGIN
        SET NOCOUNT ON;
        BEGIN TRY
            BEGIN TRANSACTION;
            DECLARE @StockID BIGINT;
            DECLARE @CurrentQty DECIMAL(18,3) = 0;
            DECLARE @BatchID BIGINT = NULL;

            SELECT @StockID = StockID, @CurrentQty = QuantityOnHand
            FROM dbo.Stock
            WHERE CompanyID = @CompanyID AND ProductID = @ProductID 
              AND WarehouseID = @WarehouseID AND ISNULL(LocationID, -1) = ISNULL(@LocationID, -1) AND IsDeleted = 0;

            IF @StockID IS NULL
            BEGIN
                INSERT INTO dbo.Stock (CompanyID, WarehouseID, LocationID, ProductID, QuantityOnHand, CreatedBy)
                VALUES (@CompanyID, @WarehouseID, @LocationID, @ProductID, 0, @CreatedBy);
                SET @StockID = SCOPE_IDENTITY();
                SET @CurrentQty = 0;
            END

            IF @BatchNumber IS NOT NULL
            BEGIN
                MERGE INTO dbo.WarehouseBatches AS target
                USING (SELECT @CompanyID AS CID, @ProductID AS PID, @WarehouseID AS WID, @LocationID AS LID, @BatchNumber AS BNO) AS source
                ON target.CompanyID = source.CID AND target.ProductID = source.PID 
                   AND target.BatchNumber = source.BNO AND target.IsDeleted = 0
                WHEN MATCHED THEN
                    UPDATE SET CurrentQuantity = CurrentQuantity + @Quantity,
                               UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @CreatedBy
                WHEN NOT MATCHED THEN
                    INSERT (CompanyID, ProductID, WarehouseID, LocationID, BatchNumber, ExpiryDate, 
                            OriginalQuantity, CurrentQuantity, PurchaseCost, ZATCAComplianceStatus, CreatedBy)
                    VALUES (@CompanyID, @ProductID, @WarehouseID, @LocationID, @BatchNumber, @ExpiryDate,
                            @Quantity, @Quantity, @UnitCost, @ZATCAStatus, @CreatedBy);
                SELECT @BatchID = BatchID FROM dbo.WarehouseBatches WHERE CompanyID = @CompanyID AND BatchNumber = @BatchNumber AND IsDeleted = 0;
            END

            DECLARE @NewQty DECIMAL(18,3) = @CurrentQty + @Quantity;
            UPDATE dbo.Stock 
            SET QuantityOnHand = @NewQty,
                LastTransactionDate = SYSUTCDATETIME(),
                LastTransactionType = @TransactionType,
                UpdatedBy = @CreatedBy, UpdatedAt = SYSUTCDATETIME()
            WHERE StockID = @StockID;
            SET @NewBalance = @NewQty;

            INSERT INTO dbo.StockTransactions (
                CompanyID, StockID, TransactionType, Quantity, QuantityBefore, QuantityAfter, 
                UnitCost, ReferenceTable, ReferenceID, ReferenceLineID, ZATCAComplianceStatus, Notes, CreatedBy
            ) VALUES (
                @CompanyID, @StockID, @TransactionType, @Quantity, @CurrentQty, @NewQty, @UnitCost,
                @ReferenceTable, @ReferenceID, @ReferenceLineID, @ZATCAStatus, @Notes, @CreatedBy
            );
            COMMIT TRANSACTION;
        END TRY
        BEGIN CATCH
            IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
            THROW;
        END CATCH
    END;
    PRINT N'✅ [9.1] usp_Stock_Increase created.';

    IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Stock_Decrease')
        DROP PROCEDURE dbo.usp_Stock_Decrease;
    GO
    CREATE PROCEDURE dbo.usp_Stock_Decrease
        @CompanyID UNIQUEIDENTIFIER,
        @ProductID INT,
        @WarehouseID BIGINT,
        @LocationID BIGINT = NULL,
        @Quantity DECIMAL(18,3),
        @TransactionType NVARCHAR(30),
        @UnitCost DECIMAL(18,6) = NULL,
        @BatchNumber NVARCHAR(50) = NULL,
        @ZATCAStatus NVARCHAR(20) = 'PENDING',
        @ReferenceTable NVARCHAR(50) = NULL,
        @ReferenceID BIGINT = NULL,
        @ReferenceLineID BIGINT = NULL,
        @Notes NVARCHAR(MAX) = NULL,
        @CreatedBy UNIQUEIDENTIFIER,
        @NewBalance DECIMAL(18,3) OUTPUT
    AS
    BEGIN
        SET NOCOUNT ON;
        BEGIN TRY
            BEGIN TRANSACTION;
            DECLARE @StockID BIGINT;
            DECLARE @CurrentQty DECIMAL(18,3) = 0;
            DECLARE @AllowNegative BIT;

            SELECT @StockID = StockID, @CurrentQty = QuantityOnHand
            FROM dbo.Stock
            WHERE CompanyID = @CompanyID AND ProductID = @ProductID 
              AND WarehouseID = @WarehouseID AND ISNULL(LocationID, -1) = ISNULL(@LocationID, -1) AND IsDeleted = 0;

            IF @StockID IS NULL THROW 50000, 'لا يوجد مخزون لهذا المنتج.', 1;

            SELECT @AllowNegative = AllowNegativeStock FROM dbo.Warehouses WHERE WarehouseID = @WarehouseID;
            IF @AllowNegative = 0 AND @CurrentQty < @Quantity
                THROW 50000, N'الكمية المطلوبة أكبر من الرصيد المتاح.', 1;

            IF @BatchNumber IS NOT NULL
            BEGIN
                UPDATE dbo.WarehouseBatches 
                SET CurrentQuantity = CurrentQuantity - @Quantity,
                    IsFullyConsumed = CASE WHEN CurrentQuantity - @Quantity <= 0 THEN 1 ELSE 0 END,
                    UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @CreatedBy
                WHERE CompanyID = @CompanyID AND ProductID = @ProductID 
                  AND BatchNumber = @BatchNumber AND IsDeleted = 0;
            END

            DECLARE @NewQty DECIMAL(18,3) = @CurrentQty - @Quantity;
            UPDATE dbo.Stock 
            SET QuantityOnHand = @NewQty,
                LastTransactionDate = SYSUTCDATETIME(),
                LastTransactionType = @TransactionType,
                UpdatedBy = @CreatedBy, UpdatedAt = SYSUTCDATETIME()
            WHERE StockID = @StockID;
            SET @NewBalance = @NewQty;

            INSERT INTO dbo.StockTransactions (
                CompanyID, StockID, TransactionType, Quantity, QuantityBefore, QuantityAfter, 
                UnitCost, ReferenceTable, ReferenceID, ReferenceLineID, ZATCAComplianceStatus, Notes, CreatedBy
            ) VALUES (
                @CompanyID, @StockID, @TransactionType, @Quantity, @CurrentQty, @NewQty, @UnitCost,
                @ReferenceTable, @ReferenceID, @ReferenceLineID, @ZATCAStatus, @Notes, @CreatedBy
            );
            COMMIT TRANSACTION;
        END TRY
        BEGIN CATCH
            IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
            THROW;
        END CATCH
    END;
    PRINT N'✅ [9.2] usp_Stock_Decrease created.';

    IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Stock_MoveLocation')
        DROP PROCEDURE dbo.usp_Stock_MoveLocation;
    GO
    CREATE PROCEDURE dbo.usp_Stock_MoveLocation
        @CompanyID UNIQUEIDENTIFIER,
        @ProductID INT,
        @FromLocationID BIGINT,
        @ToLocationID BIGINT,
        @Quantity DECIMAL(18,3),
        @CreatedBy UNIQUEIDENTIFIER,
        @Notes NVARCHAR(MAX) = NULL
    AS
    BEGIN
        SET NOCOUNT ON;
        BEGIN TRY
            BEGIN TRANSACTION;
            DECLARE @NewBalance DECIMAL(18,3);
            EXEC dbo.usp_Stock_Decrease 
                @CompanyID, @ProductID, (SELECT WarehouseID FROM dbo.WarehouseLocations WHERE LocationID = @FromLocationID),
                @FromLocationID, @Quantity, 'LOCATION_MOVE', NULL, NULL, 'PENDING',
                'LocationMove', NULL, NULL, @Notes + N' (انتقال من ' + CAST(@FromLocationID AS NVARCHAR) + N' إلى ' + CAST(@ToLocationID AS NVARCHAR) + N')',
                @CreatedBy, @NewBalance OUTPUT;

            EXEC dbo.usp_Stock_Increase 
                @CompanyID, @ProductID, (SELECT WarehouseID FROM dbo.WarehouseLocations WHERE LocationID = @ToLocationID),
                @ToLocationID, @Quantity, 'LOCATION_MOVE', NULL, NULL, NULL, 'PENDING',
                'LocationMove', NULL, NULL, @Notes + N' (انتقال من ' + CAST(@FromLocationID AS NVARCHAR) + N' إلى ' + CAST(@ToLocationID AS NVARCHAR) + N')',
                @CreatedBy, @NewBalance OUTPUT;
            COMMIT TRANSACTION;
        END TRY
        BEGIN CATCH
            IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
            THROW;
        END CATCH
    END;
    PRINT N'✅ [9.3] usp_Stock_MoveLocation created.';

    IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Stock_Adjustment')
        DROP PROCEDURE dbo.usp_Stock_Adjustment;
    GO
    CREATE PROCEDURE dbo.usp_Stock_Adjustment
        @CompanyID UNIQUEIDENTIFIER,
        @ProductID INT,
        @WarehouseID BIGINT,
        @LocationID BIGINT = NULL,
        @PhysicalQuantity DECIMAL(18,3),
        @BatchNumber NVARCHAR(50) = NULL,
        @CountID BIGINT = NULL,
        @Notes NVARCHAR(MAX) = NULL,
        @CreatedBy UNIQUEIDENTIFIER,
        @AdjustedQuantity DECIMAL(18,3) OUTPUT
    AS
    BEGIN
        SET NOCOUNT ON;
        BEGIN TRY
            BEGIN TRANSACTION;
            DECLARE @CurrentQty DECIMAL(18,3) = 0;
            DECLARE @StockID BIGINT;

            SELECT @StockID = StockID, @CurrentQty = QuantityOnHand
            FROM dbo.Stock
            WHERE CompanyID = @CompanyID AND ProductID = @ProductID 
              AND WarehouseID = @WarehouseID AND ISNULL(LocationID, -1) = ISNULL(@LocationID, -1) AND IsDeleted = 0;

            IF @StockID IS NULL THROW 50000, 'لا يوجد سجل مخزون لهذا المنتج.', 1;

            SET @AdjustedQuantity = @PhysicalQuantity - @CurrentQty;
            IF @AdjustedQuantity = 0
            BEGIN
                COMMIT TRANSACTION;
                RETURN;
            END

            UPDATE dbo.Stock 
            SET QuantityOnHand = @PhysicalQuantity,
                LastTransactionDate = SYSUTCDATETIME(),
                LastTransactionType = 'ADJUSTMENT',
                UpdatedBy = @CreatedBy, UpdatedAt = SYSUTCDATETIME()
            WHERE StockID = @StockID;

            INSERT INTO dbo.StockTransactions (
                CompanyID, StockID, TransactionType, Quantity, QuantityBefore, QuantityAfter,
                ReferenceTable, ReferenceID, ZATCAComplianceStatus, Notes, CreatedBy
            ) VALUES (
                @CompanyID, @StockID, 'ADJUSTMENT', @AdjustedQuantity, @CurrentQty, @PhysicalQuantity,
                'WarehouseCounts', @CountID, 'PENDING',
                CASE WHEN @AdjustedQuantity > 0 THEN N'زيادة في الجرد' ELSE N'نقص في الجرد' END + ISNULL(' - ' + @Notes, ''),
                @CreatedBy
            );
            COMMIT TRANSACTION;
        END TRY
        BEGIN CATCH
            IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
            THROW;
        END CATCH
    END;
    PRINT N'✅ [9.4] usp_Stock_Adjustment created.';

    IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Warehouse_StartCount')
        DROP PROCEDURE dbo.usp_Warehouse_StartCount;
    GO
    CREATE PROCEDURE dbo.usp_Warehouse_StartCount
        @WarehouseID BIGINT,
        @CountType NVARCHAR(20) = 'FULL',
        @CountedByUserID UNIQUEIDENTIFIER,
        @SupervisedByUserID UNIQUEIDENTIFIER = NULL,
        @CreatedBy UNIQUEIDENTIFIER,
        @NewCountID BIGINT OUTPUT
    AS
    BEGIN
        SET NOCOUNT ON;
        BEGIN TRY
            BEGIN TRANSACTION;
            UPDATE dbo.Warehouses 
            SET IsFrozen = 1, FrozenAt = SYSUTCDATETIME(), FrozenBy = @CreatedBy,
                UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @CreatedBy
            WHERE WarehouseID = @WarehouseID AND IsActive = 1 AND IsDeleted = 0;

            DECLARE @CountNumber NVARCHAR(50);
            SET @CountNumber = 'CNT-' + FORMAT(GETDATE(), 'yyyyMMdd') + '-' + 
                CAST((SELECT ISNULL(MAX(CAST(SUBSTRING(CountNumber, CHARINDEX('-', CountNumber, 10)+1, LEN(CountNumber)) AS INT)), 0) + 1 
                      FROM dbo.WarehouseCounts 
                      WHERE WarehouseID = @WarehouseID AND CountNumber LIKE 'CNT-' + FORMAT(GETDATE(), 'yyyyMMdd') + '-%') AS NVARCHAR(10));

            INSERT INTO dbo.WarehouseCounts (
                WarehouseID, CountNumber, CountDate, CountType, CountStatus,
                StartedAt, CountedByUserID, SupervisedByUserID, CreatedBy
            ) VALUES (
                @WarehouseID, @CountNumber, CAST(GETDATE() AS DATE), @CountType, 'OPEN',
                SYSUTCDATETIME(), @CountedByUserID, @SupervisedByUserID, @CreatedBy
            );
            SET @NewCountID = SCOPE_IDENTITY();
            COMMIT TRANSACTION;
        END TRY
        BEGIN CATCH
            IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
            THROW;
        END CATCH
    END;
    PRINT N'✅ [9.5] usp_Warehouse_StartCount created.';

    IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Warehouse_CompleteCount')
        DROP PROCEDURE dbo.usp_Warehouse_CompleteCount;
    GO
    CREATE PROCEDURE dbo.usp_Warehouse_CompleteCount
        @CountID BIGINT,
        @AdjustStock BIT = 1,
        @CompletedBy UNIQUEIDENTIFIER
    AS
    BEGIN
        SET NOCOUNT ON;
        BEGIN TRY
            BEGIN TRANSACTION;
            DECLARE @WarehouseID BIGINT;
            SELECT @WarehouseID = WarehouseID FROM dbo.WarehouseCounts WHERE CountID = @CountID AND IsDeleted = 0;
            IF @WarehouseID IS NULL THROW 50000, 'سجل الجرد غير موجود.', 1;

            IF @AdjustStock = 1
            BEGIN
                DECLARE @ProductID INT, @PhysicalQty DECIMAL(18,3), @ExpectedQty DECIMAL(18,3);
                DECLARE @AdjQty DECIMAL(18,3);
                DECLARE cur CURSOR FOR
                    SELECT ProductID, PhysicalQuantity, ExpectedQuantity
                    FROM dbo.WarehouseCountItems
                    WHERE CountID = @CountID AND IsDeleted = 0 AND PhysicalQuantity <> ExpectedQuantity;
                OPEN cur;
                FETCH NEXT FROM cur INTO @ProductID, @PhysicalQty, @ExpectedQty;
                WHILE @@FETCH_STATUS = 0
                BEGIN
                    EXEC dbo.usp_Stock_Adjustment 
                        @CompanyID = (SELECT CompanyID FROM dbo.Warehouses WHERE WarehouseID = @WarehouseID),
                        @ProductID = @ProductID,
                        @WarehouseID = @WarehouseID,
                        @LocationID = NULL,
                        @PhysicalQuantity = @PhysicalQty,
                        @BatchNumber = NULL,
                        @CountID = @CountID,
                        @Notes = N'تسوية تلقائية من الجرد الدوري',
                        @CreatedBy = @CompletedBy,
                        @AdjustedQuantity = @AdjQty OUTPUT;
                    FETCH NEXT FROM cur INTO @ProductID, @PhysicalQty, @ExpectedQty;
                END
                CLOSE cur; DEALLOCATE cur;
            END

            UPDATE dbo.WarehouseCounts 
            SET CountStatus = 'COMPLETED', CompletedAt = SYSUTCDATETIME(),
                UpdatedBy = @CompletedBy, UpdatedAt = SYSUTCDATETIME()
            WHERE CountID = @CountID;

            UPDATE dbo.Warehouses 
            SET IsFrozen = 0, FrozenAt = NULL, FrozenBy = NULL,
                UpdatedBy = @CompletedBy, UpdatedAt = SYSUTCDATETIME()
            WHERE WarehouseID = @WarehouseID;

            COMMIT TRANSACTION;
        END TRY
        BEGIN CATCH
            IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
            THROW;
        END CATCH
    END;
    PRINT N'✅ [9.6] usp_Warehouse_CompleteCount created.';

    IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Warehouse_Transfer')
        DROP PROCEDURE dbo.usp_Warehouse_Transfer;
    GO
    CREATE PROCEDURE dbo.usp_Warehouse_Transfer
        @CompanyID UNIQUEIDENTIFIER,
        @ProductID INT,
        @FromWarehouseID BIGINT,
        @ToWarehouseID BIGINT,
        @Quantity DECIMAL(18,3),
        @FromLocationID BIGINT = NULL,
        @ToLocationID BIGINT = NULL,
        @BatchNumber NVARCHAR(50) = NULL,
        @TransferType NVARCHAR(20) = 'DIRECT',
        @ExpectedArrivalDate DATE = NULL,
        @Notes NVARCHAR(MAX) = NULL,
        @CreatedBy UNIQUEIDENTIFIER,
        @TransferID BIGINT OUTPUT
    AS
    BEGIN
        SET NOCOUNT ON;
        BEGIN TRY
            BEGIN TRANSACTION;
            DECLARE @TransferNumber NVARCHAR(50);
            SET @TransferNumber = 'TRF-' + FORMAT(GETDATE(), 'yyyyMMdd') + '-' + 
                CAST((SELECT ISNULL(MAX(CAST(SUBSTRING(TransferNumber, CHARINDEX('-', TransferNumber, 10)+1, LEN(TransferNumber)) AS INT)), 0) + 1 
                      FROM dbo.WarehouseTransfers 
                      WHERE CompanyID = @CompanyID AND TransferNumber LIKE 'TRF-' + FORMAT(GETDATE(), 'yyyyMMdd') + '-%') AS NVARCHAR(10));

            INSERT INTO dbo.WarehouseTransfers (
                CompanyID, TransferNumber, FromWarehouseID, ToWarehouseID, FromLocationID, ToLocationID,
                TransferDate, TransferType, TransferStatus, ExpectedArrivalDate, Notes, CreatedBy
            ) VALUES (
                @CompanyID, @TransferNumber, @FromWarehouseID, @ToWarehouseID, @FromLocationID, @ToLocationID,
                CAST(GETDATE() AS DATE), @TransferType, 'DRAFT', @ExpectedArrivalDate, @Notes, @CreatedBy
            );
            SET @TransferID = SCOPE_IDENTITY();

            DECLARE @NewBalance DECIMAL(18,3);
            EXEC dbo.usp_Stock_Decrease 
                @CompanyID, @ProductID, @FromWarehouseID, @FromLocationID, @Quantity,
                'TRANSFER_OUT', NULL, @BatchNumber, 'PENDING',
                'WarehouseTransfers', @TransferID, NULL,
                @Notes + N' (نقل إلى المستودع ' + CAST(@ToWarehouseID AS NVARCHAR) + N')',
                @CreatedBy, @NewBalance OUTPUT;

            EXEC dbo.usp_Stock_Increase 
                @CompanyID, @ProductID, @ToWarehouseID, @ToLocationID, @Quantity,
                'TRANSFER_IN', NULL, @BatchNumber, NULL, 'PENDING',
                'WarehouseTransfers', @TransferID, NULL,
                @Notes + N' (نقل من المستودع ' + CAST(@FromWarehouseID AS NVARCHAR) + N')',
                @CreatedBy, @NewBalance OUTPUT;

            UPDATE dbo.WarehouseTransfers SET TransferStatus = 'COMPLETED', ReceivedAt = SYSUTCDATETIME()
            WHERE TransferID = @TransferID;
            COMMIT TRANSACTION;
        END TRY
        BEGIN CATCH
            IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
            THROW;
        END CATCH
    END;
    PRINT N'✅ [9.7] usp_Warehouse_Transfer created.';

    IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Warehouse_OccupancyReport')
        DROP PROCEDURE dbo.usp_Warehouse_OccupancyReport;
    GO
    CREATE PROCEDURE dbo.usp_Warehouse_OccupancyReport
        @CompanyID UNIQUEIDENTIFIER,
        @WarehouseID BIGINT = NULL
    AS
    BEGIN
        SET NOCOUNT ON;
        SELECT 
            w.WarehouseID,
            w.WarehouseCode,
            w.WarehouseNameAR,
            w.MaxCapacity,
            w.CurrentOccupancy,
            ISNULL(w.CurrentOccupancy / NULLIF(w.MaxCapacity, 0) * 100, 0) AS OccupancyPercentage,
            COUNT(DISTINCT l.LocationID) AS TotalLocations,
            COUNT(DISTINCT CASE WHEN l.IsActive = 1 AND l.IsBlocked = 0 THEN l.LocationID END) AS ActiveLocations,
            COUNT(DISTINCT CASE WHEN l.IsBlocked = 1 THEN l.LocationID END) AS BlockedLocations,
            COUNT(DISTINCT s.ProductID) AS UniqueProducts,
            SUM(s.QuantityOnHand) AS TotalUnitsOnHand
        FROM dbo.Warehouses w
        LEFT JOIN dbo.WarehouseLocations l ON w.WarehouseID = l.WarehouseID AND l.IsDeleted = 0
        LEFT JOIN dbo.Stock s ON w.WarehouseID = s.WarehouseID AND s.IsDeleted = 0
        WHERE w.CompanyID = @CompanyID AND w.IsDeleted = 0
          AND (@WarehouseID IS NULL OR w.WarehouseID = @WarehouseID)
        GROUP BY w.WarehouseID, w.WarehouseCode, w.WarehouseNameAR, w.MaxCapacity, w.CurrentOccupancy
        ORDER BY w.WarehouseNameAR;
    END;
    PRINT N'✅ [9.8] usp_Warehouse_OccupancyReport created.';

    IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Stock_LocationMovementReport')
        DROP PROCEDURE dbo.usp_Stock_LocationMovementReport;
    GO
    CREATE PROCEDURE dbo.usp_Stock_LocationMovementReport
        @CompanyID UNIQUEIDENTIFIER,
        @FromDate DATE = NULL,
        @ToDate DATE = NULL,
        @WarehouseID BIGINT = NULL
    AS
    BEGIN
        SET NOCOUNT ON;
        IF @FromDate IS NULL SET @FromDate = DATEADD(MONTH, -1, GETDATE());
        IF @ToDate IS NULL SET @ToDate = GETDATE();

        SELECT 
            st.TransactionID,
            st.TransactionType,
            st.CreatedAt AS MovementDate,
            p.ProductCode,
            p.ProductNameAR,
            st.Quantity,
            st.QuantityBefore,
            st.QuantityAfter,
            st.UnitCost,
            wf.WarehouseNameAR AS FromWarehouse,
            wt.WarehouseNameAR AS ToWarehouse,
            lf.LocationCode AS FromLocation,
            lt.LocationCode AS ToLocation,
            st.Notes
        FROM dbo.StockTransactions st
        INNER JOIN dbo.Stock s ON st.StockID = s.StockID
        INNER JOIN dbo.Products p ON s.ProductID = p.ProductID
        LEFT JOIN dbo.Warehouses wf ON s.WarehouseID = wf.WarehouseID
        LEFT JOIN dbo.Warehouses wt ON s.WarehouseID = wt.WarehouseID
        LEFT JOIN dbo.WarehouseLocations lf ON s.LocationID = lf.LocationID
        LEFT JOIN dbo.WarehouseLocations lt ON s.LocationID = lt.LocationID
        WHERE st.CompanyID = @CompanyID AND st.IsDeleted = 0
          AND st.TransactionType = 'LOCATION_MOVE'
          AND (@WarehouseID IS NULL OR s.WarehouseID = @WarehouseID)
          AND CAST(st.CreatedAt AS DATE) BETWEEN @FromDate AND @ToDate
        ORDER BY st.CreatedAt DESC;
    END;
    PRINT N'✅ [9.9] usp_Stock_LocationMovementReport created.';

    IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Warehouse_BatchExpiryReport')
        DROP PROCEDURE dbo.usp_Warehouse_BatchExpiryReport;
    GO
    CREATE PROCEDURE dbo.usp_Warehouse_BatchExpiryReport
        @CompanyID UNIQUEIDENTIFIER,
        @DaysThreshold INT = 30,
        @WarehouseID BIGINT = NULL,
        @ProductID INT = NULL
    AS
    BEGIN
        SET NOCOUNT ON;
        SELECT 
            b.BatchID,
            b.ProductID,
            p.ProductCode,
            p.ProductNameAR,
            b.BatchNumber,
            b.WarehouseID,
            w.WarehouseNameAR AS WarehouseName,
            b.LocationID,
            l.LocationCode,
            b.ExpiryDate,
            DATEDIFF(DAY, GETDATE(), b.ExpiryDate) AS DaysRemaining,
            b.CurrentQuantity,
            b.ReservedQuantity,
            b.CurrentQuantity - b.ReservedQuantity AS AvailableQuantity,
            b.ZATCAComplianceStatus,
            CASE 
                WHEN b.ExpiryDate < GETDATE() THEN N'منتهي الصلاحية'
                WHEN DATEDIFF(DAY, GETDATE(), b.ExpiryDate) <= @DaysThreshold THEN N'ينتهي خلال ' + CAST(@DaysThreshold AS NVARCHAR) + N' يوم'
                ELSE N'صالحة'
            END AS ExpiryStatus
        FROM dbo.WarehouseBatches b
        INNER JOIN dbo.Products p ON b.ProductID = p.ProductID
        INNER JOIN dbo.Warehouses w ON b.WarehouseID = w.WarehouseID
        LEFT JOIN dbo.WarehouseLocations l ON b.LocationID = l.LocationID
        WHERE b.CompanyID = @CompanyID
            AND b.IsDeleted = 0 AND b.IsActive = 1
            AND b.ExpiryDate IS NOT NULL
            AND (@WarehouseID IS NULL OR b.WarehouseID = @WarehouseID)
            AND (@ProductID IS NULL OR b.ProductID = @ProductID)
            AND b.CurrentQuantity > 0
        ORDER BY b.ExpiryDate;
    END;
    PRINT N'✅ [9.10] usp_Warehouse_BatchExpiryReport created.';

-- ========================================================================
-- ========================================================================

    IF EXISTS (SELECT 1 FROM sys.views WHERE name = 'vw_WarehouseSummary')
        DROP VIEW dbo.vw_WarehouseSummary;
    GO
    CREATE VIEW dbo.vw_WarehouseSummary
    AS
    SELECT 
        w.WarehouseID,
        w.WarehouseCode,
        w.WarehouseNameAR,
        w.WarehouseNameEN,
        w.BranchID,
        wt.TypeNameAR AS WarehouseType,
        w.City,
        w.Region,
        w.IsDefault,
        w.IsConsignment,
        w.IsFrozen,
        w.IsActive,
        w.MaxCapacity,
        w.CurrentOccupancy,
        ISNULL(w.CurrentOccupancy / NULLIF(w.MaxCapacity, 0) * 100, 0) AS OccupancyPercentage,
        (SELECT COUNT(*) FROM dbo.Stock s WHERE s.WarehouseID = w.WarehouseID AND s.QuantityOnHand > 0) AS ItemsWithStock,
        (SELECT SUM(s.QuantityOnHand) FROM dbo.Stock s WHERE s.WarehouseID = w.WarehouseID) AS TotalUnits,
        (SELECT COUNT(*) FROM dbo.WarehouseLocations l WHERE l.WarehouseID = w.WarehouseID AND l.IsActive = 1 AND l.IsDeleted = 0) AS TotalLocations
    FROM dbo.Warehouses w
    LEFT JOIN dbo.WarehouseTypes wt ON w.WarehouseTypeID = wt.WarehouseTypeID
    WHERE w.IsDeleted = 0;
    PRINT N'✅ [10.1] vw_WarehouseSummary created.';

    IF EXISTS (SELECT 1 FROM sys.views WHERE name = 'vw_WarehouseStatus')
        DROP VIEW dbo.vw_WarehouseStatus;
    GO
    CREATE VIEW dbo.vw_WarehouseStatus
    AS
    SELECT 
        w.WarehouseID,
        w.WarehouseCode,
        w.WarehouseNameAR,
        CASE 
            WHEN w.IsFrozen = 1 THEN N'مجمّد (جرد)'
            WHEN w.IsActive = 0 THEN N'غير نشط'
            WHEN w.IsDeleted = 1 THEN N'محذوف'
            ELSE N'نشط'
        END AS Status,
        w.FrozenAt,
        (SELECT COUNT(*) FROM dbo.WarehouseTransfers t 
         WHERE (t.FromWarehouseID = w.WarehouseID OR t.ToWarehouseID = w.WarehouseID) AND t.TransferStatus = 'IN_TRANSIT') AS PendingTransfers,
        (SELECT COUNT(*) FROM dbo.WarehouseCounts c 
         WHERE c.WarehouseID = w.WarehouseID AND c.CountStatus IN ('OPEN', 'IN_PROGRESS')) AS OpenCounts
    FROM dbo.Warehouses w
    WHERE w.IsDeleted = 0;
    PRINT N'✅ [10.2] vw_WarehouseStatus created.';

    IF EXISTS (SELECT 1 FROM sys.views WHERE name = 'vw_LocationOccupancy')
        DROP VIEW dbo.vw_LocationOccupancy;
    GO
    CREATE VIEW dbo.vw_LocationOccupancy
    AS
    SELECT 
        l.LocationID,
        l.LocationCode,
        l.LocationName,
        l.Aisle,
        l.Rack,
        l.Shelf,
        l.Bin,
        l.ZoneID,
        z.ZoneNameAR AS ZoneName,
        l.WarehouseID,
        w.WarehouseNameAR AS WarehouseName,
        l.MaxWeight,
        l.MaxVolume,
        l.CurrentWeight,
        l.CurrentVolume,
        ISNULL(l.CurrentWeight / NULLIF(l.MaxWeight, 0) * 100, 0) AS WeightOccupancy,
        ISNULL(l.CurrentVolume / NULLIF(l.MaxVolume, 0) * 100, 0) AS VolumeOccupancy,
        l.IsActive,
        l.IsBlocked,
        (SELECT COUNT(*) FROM dbo.Stock s WHERE s.LocationID = l.LocationID AND s.QuantityOnHand > 0) AS ItemsCount
    FROM dbo.WarehouseLocations l
    LEFT JOIN dbo.WarehouseZones z ON l.ZoneID = z.ZoneID
    LEFT JOIN dbo.Warehouses w ON l.WarehouseID = w.WarehouseID
    WHERE l.IsDeleted = 0;
    PRINT N'✅ [10.3] vw_LocationOccupancy created.';

    IF EXISTS (SELECT 1 FROM sys.views WHERE name = 'vw_BatchStatus')
        DROP VIEW dbo.vw_BatchStatus;
    GO
    CREATE VIEW dbo.vw_BatchStatus
    AS
    SELECT 
        b.BatchID,
        b.BatchNumber,
        b.ProductID,
        p.ProductCode,
        p.ProductNameAR,
        b.WarehouseID,
        w.WarehouseNameAR,
        b.LocationID,
        l.LocationCode,
        b.ExpiryDate,
        DATEDIFF(DAY, GETDATE(), b.ExpiryDate) AS DaysRemaining,
        b.CurrentQuantity,
        b.ReservedQuantity,
        b.CurrentQuantity - b.ReservedQuantity AS AvailableQuantity,
        b.ZATCAComplianceStatus,
        CASE 
            WHEN b.ExpiryDate < GETDATE() THEN N'منتهي الصلاحية'
            WHEN DATEDIFF(DAY, GETDATE(), b.ExpiryDate) <= 30 THEN N'ينتهي خلال 30 يوم'
            ELSE N'صالحة'
        END AS ExpiryStatus
    FROM dbo.WarehouseBatches b
    INNER JOIN dbo.Products p ON b.ProductID = p.ProductID
    INNER JOIN dbo.Warehouses w ON b.WarehouseID = w.WarehouseID
    LEFT JOIN dbo.WarehouseLocations l ON b.LocationID = l.LocationID
    WHERE b.IsDeleted = 0 AND b.IsActive = 1;
    PRINT N'✅ [10.4] vw_BatchStatus created.';

-- ========================================================================
-- ========================================================================
    DECLARE @SystemCompanyID UNIQUEIDENTIFIER;
    DECLARE @SystemUserID UNIQUEIDENTIFIER = '00000000-0000-0000-0000-000000000001';
    SELECT TOP 1 @SystemCompanyID = CompanyID FROM MST.dbo.Companies;

    IF NOT EXISTS (SELECT 1 FROM dbo.WarehouseTypes WHERE TypeCode = 'MAIN' AND CompanyID = @SystemCompanyID)
    BEGIN
        INSERT INTO dbo.WarehouseTypes (CompanyID, TypeCode, TypeNameAR, TypeNameEN, CreatedBy)
        VALUES 
            (@SystemCompanyID, 'MAIN', N'مستودع رئيسي', N'Main Warehouse', @SystemUserID),
            (@SystemCompanyID, 'BRANCH', N'مستودع فرعي', N'Branch Warehouse', @SystemUserID),
            (@SystemCompanyID, 'TRANSIT', N'مستودع انتقالي', N'Transit Warehouse', @SystemUserID),
            (@SystemCompanyID, 'DAMAGED', N'مستودع تالف', N'Damaged Goods', @SystemUserID),
            (@SystemCompanyID, 'QUARANTINE', N'مستودع حجر صحي', N'Quarantine', @SystemUserID);
        PRINT N'✅ [11] Warehouse Types seeded.';
    END

    IF NOT EXISTS (SELECT 1 FROM dbo.Warehouses WHERE WarehouseCode = 'MAIN' AND CompanyID = @SystemCompanyID)
    BEGIN
        DECLARE @MainTypeID INT = (SELECT WarehouseTypeID FROM dbo.WarehouseTypes WHERE TypeCode = 'MAIN' AND CompanyID = @SystemCompanyID);
        INSERT INTO dbo.Warehouses (
            CompanyID, WarehouseTypeID, WarehouseCode, WarehouseNameAR, WarehouseNameEN,
            City, Region, Country, IsDefault, IsActive, CreatedBy
        ) VALUES (
            @SystemCompanyID, @MainTypeID, 'MAIN', N'المستودع الرئيسي', N'Main Warehouse',
            N'الرياض', N'الرياض', N'المملكة العربية السعودية', 1, 1, @SystemUserID
        );
        PRINT N'✅ [11] Default Main Warehouse seeded.';
    END

    DECLARE @MainWarehouseID BIGINT = (SELECT WarehouseID FROM dbo.Warehouses WHERE WarehouseCode = 'MAIN' AND CompanyID = @SystemCompanyID);
    IF @MainWarehouseID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.WarehouseLocations WHERE WarehouseID = @MainWarehouseID)
    BEGIN
        INSERT INTO dbo.WarehouseZones (CompanyID, WarehouseID, ZoneCode, ZoneNameAR, ZoneNameEN, ZoneType, IsDefault, CreatedBy)
        VALUES 
            (@SystemCompanyID, @MainWarehouseID, 'RECV', N'منطقة الاستلام', N'Receiving Area', 'RECEIVING', 1, @SystemUserID),
            (@SystemCompanyID, @MainWarehouseID, 'STOR', N'منطقة التخزين', N'Storage Area', 'STORAGE', 1, @SystemUserID),
            (@SystemCompanyID, @MainWarehouseID, 'PICK', N'منطقة الالتقاط', N'Picking Area', 'PICKING', 0, @SystemUserID);

        DECLARE @StorageZoneID INT = (SELECT ZoneID FROM dbo.WarehouseZones WHERE ZoneCode = 'STOR' AND WarehouseID = @MainWarehouseID);
        INSERT INTO dbo.WarehouseLocations (WarehouseID, ZoneID, LocationCode, LocationName, Aisle, Rack, Shelf, Bin, LocationType, MaxWeight, MaxVolume, CreatedBy)
        VALUES 
            (@MainWarehouseID, @StorageZoneID, 'A-01-01', N'الممر أ - رف 1 - خلية 1', 'A', '1', '1', '1', 'STORAGE', 1000, 10, @SystemUserID),
            (@MainWarehouseID, @StorageZoneID, 'A-01-02', N'الممر أ - رف 1 - خلية 2', 'A', '1', '1', '2', 'STORAGE', 1000, 10, @SystemUserID),
            (@MainWarehouseID, @StorageZoneID, 'B-02-01', N'الممر ب - رف 2 - خلية 1', 'B', '2', '1', '1', 'PICKING', 500, 5, @SystemUserID);
        PRINT N'✅ [11] Sample Zones and Locations seeded.';
    END

-- ========================================================================
-- ========================================================================
  
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    DECLARE @ErrorMessage NVARCHAR(4000) = ERROR_MESSAGE();
    DECLARE @ErrorSeverity INT = ERROR_SEVERITY();
    RAISERROR(@ErrorMessage, @ErrorSeverity, 1);
END CATCH
GO