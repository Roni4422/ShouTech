-- ========================================================================
-- FILE: dba/inv/stock_ultimate.sql
-- PROJECT: SHOUTECH ERP V10 - ULTIMATE STOCK MANAGEMENT (10.10.0)
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
PRINT N'SHOUTECH ERP V10 – ULTIMATE STOCK MANAGEMENT (10.10.0)';
PRINT N'═══════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

-- ========================================================================
-- ========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'StockBalances')
    BEGIN
        CREATE TABLE dbo.StockBalances (
            StockBalanceID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            WarehouseID BIGINT NOT NULL,
            LocationID BIGINT NULL,
            ProductID INT NOT NULL,
            
            AvailableQty DECIMAL(18,4) NOT NULL DEFAULT 0,
            ReservedQty DECIMAL(18,4) NOT NULL DEFAULT 0,
            DamagedQty DECIMAL(18,4) NOT NULL DEFAULT 0,
            OnOrderQty DECIMAL(18,4) NOT NULL DEFAULT 0,
            ReturnedQty DECIMAL(18,4) NOT NULL DEFAULT 0,
            
            AverageCost DECIMAL(18,6) NULL,
            LastCost DECIMAL(18,6) NULL,
            
            -- ZATCA
            ZATCAComplianceStatus NVARCHAR(20) NULL DEFAULT 'PENDING',
            
            LastUpdated DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            LastTransactionType NVARCHAR(30) NULL,
            LastTransactionDate DATETIME2(7) NULL,
            
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            
            CONSTRAINT PK_StockBalances PRIMARY KEY CLUSTERED (StockBalanceID),
            CONSTRAINT UQ_StockBalances_Product_Warehouse_Location 
                UNIQUE (CompanyID, WarehouseID, LocationID, ProductID),
            CONSTRAINT FK_StockBalances_Warehouse FOREIGN KEY (WarehouseID) 
                REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT FK_StockBalances_Location FOREIGN KEY (LocationID) 
                REFERENCES dbo.WarehouseLocations(LocationID),
            CONSTRAINT FK_StockBalances_Product FOREIGN KEY (ProductID) 
                REFERENCES dbo.Products(ProductID),
            CONSTRAINT CK_StockBalances_ZATCA CHECK (ZATCAComplianceStatus IN 
                ('PENDING', 'VERIFIED', 'REJECTED', NULL))
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ [1] StockBalances created.';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_StockBalances_Warehouse_Product 
        ON dbo.StockBalances(WarehouseID, ProductID) 
        INCLUDE (AvailableQty, ReservedQty, AverageCost, LocationID) WHERE IsDeleted = 0;

    CREATE NONCLUSTERED COLUMNSTORE INDEX IF NOT EXISTS CSIX_StockBalances_Analytics 
        ON dbo.StockBalances (CompanyID, WarehouseID, ProductID, AvailableQty, AverageCost);
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
            CONSTRAINT PK_Batches PRIMARY KEY CLUSTERED (BatchID),
            CONSTRAINT UQ_Batches_Number UNIQUE (CompanyID, ProductID, BatchNumber),
            CONSTRAINT FK_Batches_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID),
            CONSTRAINT FK_Batches_Warehouse FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT FK_Batches_Location FOREIGN KEY (LocationID) REFERENCES dbo.WarehouseLocations(LocationID),
            CONSTRAINT CK_Batches_ZATCA CHECK (ZATCAComplianceStatus IN ('PENDING', 'VERIFIED', 'REJECTED'))
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ [2] Batches created (ZATCA-ready).';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Batches_Expiry 
        ON dbo.Batches(ExpiryDate, ProductID) WHERE IsActive = 1 AND IsDeleted = 0;
    GO

-- ========================================================================
-- ========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SerialNumbers')
    BEGIN
        CREATE TABLE dbo.SerialNumbers (
            SerialID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ProductID INT NOT NULL,
            WarehouseID BIGINT NOT NULL,
            LocationID BIGINT NULL,
            BatchID BIGINT NULL,
            SerialNumber NVARCHAR(100) NOT NULL,
            Status NVARCHAR(20) NOT NULL DEFAULT 'IN_STOCK',
            ReceivedDate DATE NOT NULL DEFAULT CAST(GETDATE() AS DATE),
            SoldDate DATE NULL,
            CustomerID BIGINT NULL,
            CustomerName NVARCHAR(200) NULL,
            PurchaseInvoiceID BIGINT NULL,
            SalesInvoiceID BIGINT NULL,
            LastTransactionDate DATETIME2(7) NULL,
            LastTransactionType NVARCHAR(30) NULL,
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
            CONSTRAINT FK_SerialNumbers_Location FOREIGN KEY (LocationID) REFERENCES dbo.WarehouseLocations(LocationID),
            CONSTRAINT FK_SerialNumbers_Batch FOREIGN KEY (BatchID) REFERENCES dbo.Batches(BatchID),
            CONSTRAINT CK_SerialNumbers_Status CHECK (Status IN ('IN_STOCK', 'RESERVED', 'SOLD', 'RETURNED', 'DAMAGED', 'LOST'))
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ [3] SerialNumbers created.';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_SerialNumbers_Status 
        ON dbo.SerialNumbers(Status, ProductID) WHERE IsActive = 1 AND IsDeleted = 0;
    GO

-- ========================================================================
-- ========================================================================
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
            CONSTRAINT FK_FIFOLayers_Warehouse FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT FK_FIFOLayers_Batch FOREIGN KEY (BatchID) REFERENCES dbo.Batches(BatchID)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ [4] FIFOLayers created (Integration with val.sql).';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_FIFOLayers_Product_Warehouse 
        ON dbo.FIFOLayers(ProductID, WarehouseID, LayerDate) WHERE IsDeleted = 0 AND IsConsumed = 0;
    GO

-- ========================================================================
-- ========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'StockReservations')
    BEGIN
        CREATE TABLE dbo.StockReservations (
            ReservationID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            StockBalanceID BIGINT NOT NULL,
            BatchID BIGINT NULL,
            SerialID BIGINT NULL,
            ReferenceTable NVARCHAR(50) NOT NULL,
            ReferenceID BIGINT NOT NULL,
            ReferenceLineID BIGINT NULL,
            ReferenceNumber NVARCHAR(50) NULL,
            ReservedQuantity DECIMAL(18,4) NOT NULL,
            OriginalQuantity DECIMAL(18,4) NOT NULL,
            ReleasedQuantity DECIMAL(18,4) NOT NULL DEFAULT 0,
            ReservedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            ReservedBy UNIQUEIDENTIFIER NOT NULL,
            ReleasedAt DATETIME2(7) NULL,
            ReleasedBy UNIQUEIDENTIFIER NULL,
            Status NVARCHAR(20) NOT NULL DEFAULT 'ACTIVE',
            ExpiryDate DATE NULL,
            Notes NVARCHAR(MAX) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_StockReservations PRIMARY KEY CLUSTERED (ReservationID),
            CONSTRAINT FK_StockReservations_StockBalance FOREIGN KEY (StockBalanceID) 
                REFERENCES dbo.StockBalances(StockBalanceID) ON DELETE CASCADE,
            CONSTRAINT FK_StockReservations_Batch FOREIGN KEY (BatchID) REFERENCES dbo.Batches(BatchID),
            CONSTRAINT FK_StockReservations_Serial FOREIGN KEY (SerialID) REFERENCES dbo.SerialNumbers(SerialID),
            CONSTRAINT CK_StockReservations_Status CHECK (Status IN ('ACTIVE', 'PARTIAL', 'RELEASED', 'EXPIRED'))
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ [5] StockReservations created (Advanced reservations).';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_StockReservations_Reference 
        ON dbo.StockReservations(ReferenceTable, ReferenceID, Status) WHERE IsDeleted = 0;
    GO

-- ========================================================================
-- ========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'StockAdjustments')
    BEGIN
        CREATE TABLE dbo.StockAdjustments (
            AdjustmentID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            AdjustmentNumber NVARCHAR(50) NOT NULL,
            AdjustmentDate DATE NOT NULL DEFAULT CAST(GETDATE() AS DATE),
            WarehouseID BIGINT NOT NULL,
            AdjustmentType NVARCHAR(20) NOT NULL,
            Reason NVARCHAR(200) NOT NULL,
            TotalAdjustment DECIMAL(18,4) NOT NULL DEFAULT 0,
            Status NVARCHAR(20) NOT NULL DEFAULT 'DRAFT',
            CountID BIGINT NULL,
            ValuationMethodApplied NVARCHAR(20) NULL,
            PostedAt DATETIME2(7) NULL,
            PostedBy UNIQUEIDENTIFIER NULL,
            CancelledAt DATETIME2(7) NULL,
            CancelledBy UNIQUEIDENTIFIER NULL,
            Notes NVARCHAR(MAX) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_StockAdjustments PRIMARY KEY CLUSTERED (AdjustmentID),
            CONSTRAINT UQ_StockAdjustments_Number UNIQUE (CompanyID, AdjustmentNumber),
            CONSTRAINT FK_StockAdjustments_Warehouse FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT CK_StockAdjustments_Type CHECK (AdjustmentType IN ('INCREASE', 'DECREASE', 'CORRECTION')),
            CONSTRAINT CK_StockAdjustments_Status CHECK (Status IN ('DRAFT', 'POSTED', 'CANCELLED'))
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ [6] StockAdjustments created.';
    END

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'StockAdjustmentItems')
    BEGIN
        CREATE TABLE dbo.StockAdjustmentItems (
            AdjustmentItemID BIGINT IDENTITY(1,1) NOT NULL,
            AdjustmentID BIGINT NOT NULL,
            ProductID INT NOT NULL,
            BatchID BIGINT NULL,
            LocationID BIGINT NULL,
            OldQuantity DECIMAL(18,4) NOT NULL,
            NewQuantity DECIMAL(18,4) NOT NULL,
            DifferenceQuantity DECIMAL(18,4) NOT NULL,
            UnitCost DECIMAL(18,6) NULL,
            Notes NVARCHAR(200) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_StockAdjustmentItems PRIMARY KEY CLUSTERED (AdjustmentItemID),
            CONSTRAINT FK_StockAdjustmentItems_Adjustment FOREIGN KEY (AdjustmentID) 
                REFERENCES dbo.StockAdjustments(AdjustmentID) ON DELETE CASCADE,
            CONSTRAINT FK_StockAdjustmentItems_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID),
            CONSTRAINT FK_StockAdjustmentItems_Batch FOREIGN KEY (BatchID) REFERENCES dbo.Batches(BatchID),
            CONSTRAINT FK_StockAdjustmentItems_Location FOREIGN KEY (LocationID) REFERENCES dbo.WarehouseLocations(LocationID)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ [6.1] StockAdjustmentItems created.';
    END
    GO

-- ========================================================================
-- ========================================================================

IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Stock_UpdateBalance')
    DROP PROCEDURE dbo.usp_Stock_UpdateBalance;
GO
CREATE PROCEDURE dbo.usp_Stock_UpdateBalance
    @CompanyID UNIQUEIDENTIFIER,
    @ProductID INT,
    @WarehouseID BIGINT,
    @LocationID BIGINT = NULL,
    @Quantity DECIMAL(18,4),
    @IsIncrease BIT = 1,
    @BatchNumber NVARCHAR(50) = NULL,
    @SerialNumber NVARCHAR(100) = NULL,
    @ExpiryDate DATE = NULL,
    @UnitCost DECIMAL(18,6) = NULL,
    @ReferenceType NVARCHAR(30) = NULL,
    @ReferenceID BIGINT = NULL,
    @ReferenceLineID BIGINT = NULL,
    @CreatedBy UNIQUEIDENTIFIER,
    @ZATCAStatus NVARCHAR(20) = 'PENDING',
    @TaxNumber NVARCHAR(100) = NULL,
    @NewBalance DECIMAL(18,4) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        BEGIN TRANSACTION;

        DECLARE @StockBalanceID BIGINT;
        DECLARE @CurrentQty DECIMAL(18,4) = 0;
        DECLARE @CurrentAvgCost DECIMAL(18,6) = 0;
        DECLARE @BatchID BIGINT = NULL;

        IF @BatchNumber IS NOT NULL
        BEGIN
            SELECT @BatchID = BatchID FROM dbo.Batches 
            WHERE CompanyID = @CompanyID AND BatchNumber = @BatchNumber AND IsDeleted = 0;
        END

        MERGE INTO dbo.StockBalances AS target
        USING (SELECT @CompanyID AS CompanyID, @WarehouseID AS WarehouseID, 
                      @LocationID AS LocationID, @ProductID AS ProductID) AS source
        ON target.CompanyID = source.CompanyID 
           AND target.WarehouseID = source.WarehouseID 
           AND ISNULL(target.LocationID, -1) = ISNULL(source.LocationID, -1)
           AND target.ProductID = source.ProductID AND target.IsDeleted = 0
        WHEN MATCHED THEN
            UPDATE SET 
                AvailableQty = CASE WHEN @IsIncrease = 1 THEN AvailableQty + @Quantity 
                                    ELSE AvailableQty - @Quantity END,
                LastCost = CASE WHEN @UnitCost IS NOT NULL AND @IsIncrease = 1 THEN @UnitCost ELSE LastCost END,
                AverageCost = CASE 
                    WHEN @IsIncrease = 1 AND @UnitCost IS NOT NULL AND (AvailableQty + @Quantity) > 0
                    THEN ((ISNULL(AverageCost, 0) * AvailableQty) + (@UnitCost * @Quantity)) / (AvailableQty + @Quantity)
                    ELSE AverageCost END,
                LastUpdated = SYSUTCDATETIME(),
                LastTransactionType = @ReferenceType,
                LastTransactionDate = SYSUTCDATETIME(),
                ZATCAComplianceStatus = ISNULL(@ZATCAStatus, ZATCAComplianceStatus),
                UpdatedBy = @CreatedBy,
                UpdatedAt = SYSUTCDATETIME()
        WHEN NOT MATCHED THEN
            INSERT (CompanyID, WarehouseID, LocationID, ProductID, AvailableQty, AverageCost, LastCost, ZATCAComplianceStatus, CreatedBy)
            VALUES (@CompanyID, @WarehouseID, @LocationID, @ProductID, 
                    CASE WHEN @IsIncrease = 1 THEN @Quantity ELSE 0 END,
                    @UnitCost, @UnitCost, @ZATCAStatus, @CreatedBy);

        SELECT @StockBalanceID = StockBalanceID, @CurrentQty = AvailableQty
        FROM dbo.StockBalances
        WHERE CompanyID = @CompanyID AND WarehouseID = @WarehouseID 
          AND ISNULL(LocationID, -1) = ISNULL(@LocationID, -1)
          AND ProductID = @ProductID AND IsDeleted = 0;
        
        SET @NewBalance = @CurrentQty;

        IF @IsIncrease = 1
        BEGIN
            IF @UnitCost IS NOT NULL AND @Quantity > 0
            BEGIN
                INSERT INTO dbo.FIFOLayers (
                    CompanyID, ProductID, WarehouseID, BatchID, LayerDate,
                    Quantity, UnitCost, RemainingQuantity, SourceReference, SourceID, CreatedBy
                ) VALUES (
                    @CompanyID, @ProductID, @WarehouseID, @BatchID, CAST(GETDATE() AS DATE),
                    @Quantity, @UnitCost, @Quantity, @ReferenceType, @ReferenceID, @CreatedBy
                );
            END
        END
        ELSE
        BEGIN
            DECLARE @RemainingQtyToConsume DECIMAL(18,4) = @Quantity;
            DECLARE @FIFOCostTotal DECIMAL(18,6) = 0;
            DECLARE @LayerQty DECIMAL(18,4), @LayerUnitCost DECIMAL(18,6), @LayerID BIGINT;

            WHILE @RemainingQtyToConsume > 0
            BEGIN
                SELECT TOP 1 
                    @LayerID = LayerID,
                    @LayerQty = RemainingQuantity,
                    @LayerUnitCost = UnitCost
                FROM dbo.FIFOLayers
                WHERE CompanyID = @CompanyID
                    AND ProductID = @ProductID
                    AND WarehouseID = @WarehouseID
                    AND IsConsumed = 0 AND IsDeleted = 0
                ORDER BY LayerDate ASC, LayerID ASC;

                IF @LayerID IS NULL BREAK;

                IF @LayerQty >= @RemainingQtyToConsume
                BEGIN
                    SET @FIFOCostTotal = @FIFOCostTotal + (@RemainingQtyToConsume * @LayerUnitCost);
                    UPDATE dbo.FIFOLayers 
                    SET RemainingQuantity = RemainingQuantity - @RemainingQtyToConsume,
                        IsConsumed = CASE WHEN RemainingQuantity - @RemainingQtyToConsume = 0 THEN 1 ELSE 0 END,
                        ConsumedAt = CASE WHEN RemainingQuantity - @RemainingQtyToConsume = 0 THEN SYSUTCDATETIME() ELSE NULL END
                    WHERE LayerID = @LayerID;
                    SET @RemainingQtyToConsume = 0;
                END
                ELSE
                BEGIN
                    SET @FIFOCostTotal = @FIFOCostTotal + (@LayerQty * @LayerUnitCost);
                    SET @RemainingQtyToConsume = @RemainingQtyToConsume - @LayerQty;
                    UPDATE dbo.FIFOLayers 
                    SET RemainingQuantity = 0, IsConsumed = 1, ConsumedAt = SYSUTCDATETIME()
                    WHERE LayerID = @LayerID;
                END
            END

            IF @Quantity > 0 AND @FIFOCostTotal > 0 AND @RemainingQtyToConsume = 0
                SET @UnitCost = @FIFOCostTotal / @Quantity;
        END

        IF @BatchNumber IS NOT NULL
        BEGIN
            IF @IsIncrease = 1
            BEGIN
                MERGE INTO dbo.Batches AS target
                USING (SELECT @CompanyID AS CID, @ProductID AS PID, @WarehouseID AS WID, 
                              @LocationID AS LID, @BatchNumber AS BNO) AS source
                ON target.CompanyID = source.CID AND target.ProductID = source.PID 
                   AND target.BatchNumber = source.BNO AND target.IsDeleted = 0
                WHEN MATCHED THEN
                    UPDATE SET CurrentQuantity = CurrentQuantity + @Quantity,
                               UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @CreatedBy
                WHEN NOT MATCHED THEN
                    INSERT (CompanyID, ProductID, WarehouseID, LocationID, BatchNumber, 
                            ExpiryDate, OriginalQuantity, CurrentQuantity, PurchaseCost,
                            ZATCAComplianceStatus, TaxNumber, CreatedBy)
                    VALUES (@CompanyID, @ProductID, @WarehouseID, @LocationID, @BatchNumber,
                            @ExpiryDate, @Quantity, @Quantity, @UnitCost,
                            @ZATCAStatus, @TaxNumber, @CreatedBy);
            END
            ELSE
            BEGIN
                UPDATE dbo.Batches 
                SET CurrentQuantity = CurrentQuantity - @Quantity,
                    IsFullyConsumed = CASE WHEN CurrentQuantity - @Quantity <= 0 THEN 1 ELSE 0 END,
                    UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @CreatedBy
                WHERE CompanyID = @CompanyID AND ProductID = @ProductID 
                  AND BatchNumber = @BatchNumber AND IsDeleted = 0;
            END
        END

        IF @SerialNumber IS NOT NULL
        BEGIN
            IF @IsIncrease = 1
            BEGIN
                INSERT INTO dbo.SerialNumbers (
                    CompanyID, ProductID, WarehouseID, LocationID, BatchID,
                    SerialNumber, Status, ReceivedDate, CreatedBy
                ) VALUES (
                    @CompanyID, @ProductID, @WarehouseID, @LocationID, @BatchID,
                    @SerialNumber, 'IN_STOCK', CAST(GETDATE() AS DATE), @CreatedBy
                );
            END
            ELSE
            BEGIN
                UPDATE dbo.SerialNumbers 
                SET Status = 'SOLD',
                    SoldDate = CAST(GETDATE() AS DATE),
                    LastTransactionDate = SYSUTCDATETIME(),
                    LastTransactionType = @ReferenceType,
                    UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @CreatedBy
                WHERE CompanyID = @CompanyID AND ProductID = @ProductID 
                  AND SerialNumber = @SerialNumber AND IsDeleted = 0;
            END
        END

        EXEC dbo.usp_Valuation_CalculateAVG @CompanyID, @ProductID, @WarehouseID;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END;
GO
PRINT N'✅ [7.1] usp_Stock_UpdateBalance created (Integrated with FIFO).';


IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Stock_Reserve')
    DROP PROCEDURE dbo.usp_Stock_Reserve;
GO
CREATE PROCEDURE dbo.usp_Stock_Reserve
    @CompanyID UNIQUEIDENTIFIER,
    @ProductID INT,
    @WarehouseID BIGINT,
    @LocationID BIGINT = NULL,
    @Quantity DECIMAL(18,4),
    @ReferenceTable NVARCHAR(50),
    @ReferenceID BIGINT,
    @ReferenceLineID BIGINT = NULL,
    @ReferenceNumber NVARCHAR(50) = NULL,
    @ExpiryDate DATE = NULL,
    @CreatedBy UNIQUEIDENTIFIER,
    @ReservationID BIGINT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        BEGIN TRANSACTION;
        
        DECLARE @StockBalanceID BIGINT;
        DECLARE @AvailableQty DECIMAL(18,4);
        
        SELECT @StockBalanceID = StockBalanceID, @AvailableQty = AvailableQty - ReservedQty
        FROM dbo.StockBalances
        WHERE CompanyID = @CompanyID AND WarehouseID = @WarehouseID 
          AND ISNULL(LocationID, -1) = ISNULL(@LocationID, -1)
          AND ProductID = @ProductID AND IsDeleted = 0;
        
        IF @StockBalanceID IS NULL THROW 50000, 'لا يوجد رصيد مخزون.', 1;
        IF @AvailableQty < @Quantity THROW 50000, N'الكمية المتاحة غير كافية.', 1;
        
        UPDATE dbo.StockBalances
        SET ReservedQty = ReservedQty + @Quantity,
            LastUpdated = SYSUTCDATETIME(),
            LastTransactionType = 'RESERVATION',
            UpdatedBy = @CreatedBy, UpdatedAt = SYSUTCDATETIME()
        WHERE StockBalanceID = @StockBalanceID;
        
        INSERT INTO dbo.StockReservations (
            CompanyID, StockBalanceID, ReferenceTable, ReferenceID, ReferenceLineID,
            ReferenceNumber, ReservedQuantity, OriginalQuantity, ReservedBy,
            ExpiryDate, CreatedBy
        ) VALUES (
            @CompanyID, @StockBalanceID, @ReferenceTable, @ReferenceID, @ReferenceLineID,
            @ReferenceNumber, @Quantity, @Quantity, @CreatedBy,
            @ExpiryDate, @CreatedBy
        );
        SET @ReservationID = SCOPE_IDENTITY();
        
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END;
GO
PRINT N'✅ [7.2] usp_Stock_Reserve created.';


IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Stock_ReleaseReservation')
    DROP PROCEDURE dbo.usp_Stock_ReleaseReservation;
GO
CREATE PROCEDURE dbo.usp_Stock_ReleaseReservation
    @ReservationID BIGINT,
    @Quantity DECIMAL(18,4) = NULL,
    @ReleasedBy UNIQUEIDENTIFIER,
    @Notes NVARCHAR(MAX) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        BEGIN TRANSACTION;
        
        DECLARE @CompanyID UNIQUEIDENTIFIER;
        DECLARE @StockBalanceID BIGINT;
        DECLARE @ReservedQty DECIMAL(18,4), @CurrentReleased DECIMAL(18,4);
        
        SELECT @CompanyID = CompanyID, @StockBalanceID = StockBalanceID, 
               @ReservedQty = ReservedQuantity, @CurrentReleased = ReleasedQuantity
        FROM dbo.StockReservations
        WHERE ReservationID = @ReservationID AND Status = 'ACTIVE' AND IsDeleted = 0;
        
        IF @CompanyID IS NULL THROW 50000, 'الحجز غير موجود.', 1;
        
        IF @Quantity IS NULL OR @Quantity <= 0
            SET @Quantity = @ReservedQty - @CurrentReleased;
        
        IF @Quantity > (@ReservedQty - @CurrentReleased)
            THROW 50000, 'الكمية المطلوب إطلاقها أكبر من المتبقي.', 1;
        
        DECLARE @NewReleased DECIMAL(18,4) = @CurrentReleased + @Quantity;
        DECLARE @NewStatus NVARCHAR(20) = CASE WHEN @NewReleased >= @ReservedQty THEN 'RELEASED' ELSE 'PARTIAL' END;
        
        UPDATE dbo.StockReservations
        SET ReleasedQuantity = @NewReleased,
            ReleasedAt = CASE WHEN @NewStatus = 'RELEASED' THEN SYSUTCDATETIME() ELSE NULL END,
            ReleasedBy = CASE WHEN @NewStatus = 'RELEASED' THEN @ReleasedBy ELSE NULL END,
            Status = @NewStatus,
            Notes = ISNULL(Notes + CHAR(13) + CHAR(10), '') + @Notes,
            UpdatedBy = @ReleasedBy, UpdatedAt = SYSUTCDATETIME()
        WHERE ReservationID = @ReservationID;
        
        UPDATE dbo.StockBalances
        SET ReservedQty = CASE WHEN ReservedQty - @Quantity < 0 THEN 0 ELSE ReservedQty - @Quantity END,
            LastUpdated = SYSUTCDATETIME(),
            LastTransactionType = 'RELEASE',
            UpdatedBy = @ReleasedBy, UpdatedAt = SYSUTCDATETIME()
        WHERE StockBalanceID = @StockBalanceID;
        
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END;
GO
PRINT N'✅ [7.3] usp_Stock_ReleaseReservation created.';


IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Stock_RecalculateAverageCost')
    DROP PROCEDURE dbo.usp_Stock_RecalculateAverageCost;
GO
CREATE PROCEDURE dbo.usp_Stock_RecalculateAverageCost
    @CompanyID UNIQUEIDENTIFIER,
    @ProductID INT = NULL,
    @WarehouseID BIGINT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @CurrentProductID INT, @CurrentWarehouseID BIGINT;
    DECLARE @TotalCost DECIMAL(18,6), @TotalQty DECIMAL(18,4), @AvgCost DECIMAL(18,6);
    
    DECLARE cur CURSOR FOR
        SELECT DISTINCT ProductID, WarehouseID
        FROM dbo.StockBalances
        WHERE CompanyID = @CompanyID AND IsDeleted = 0 AND AvailableQty > 0
          AND (@ProductID IS NULL OR ProductID = @ProductID)
          AND (@WarehouseID IS NULL OR WarehouseID = @WarehouseID);
    
    OPEN cur;
    FETCH NEXT FROM cur INTO @CurrentProductID, @CurrentWarehouseID;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SELECT @TotalCost = SUM(Quantity * UnitCost), @TotalQty = SUM(Quantity)
        FROM dbo.StockMovements
        WHERE CompanyID = @CompanyID AND ProductID = @CurrentProductID 
          AND WarehouseIDTo = @CurrentWarehouseID
          AND MovementType IN ('PURCHASE', 'RETURN_SALE', 'TRANSFER_IN') AND IsDeleted = 0;
        
        SET @AvgCost = CASE WHEN ISNULL(@TotalQty, 0) > 0 THEN @TotalCost / @TotalQty ELSE NULL END;
        
        UPDATE dbo.StockBalances
        SET AverageCost = @AvgCost
        WHERE CompanyID = @CompanyID AND ProductID = @CurrentProductID 
          AND WarehouseID = @CurrentWarehouseID AND IsDeleted = 0;
        
        FETCH NEXT FROM cur INTO @CurrentProductID, @CurrentWarehouseID;
    END
    CLOSE cur; DEALLOCATE cur;
END;
GO
PRINT N'✅ [7.4] usp_Stock_RecalculateAverageCost created.';


IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Stock_SlowMovingReport')
    DROP PROCEDURE dbo.usp_Stock_SlowMovingReport;
GO
CREATE PROCEDURE dbo.usp_Stock_SlowMovingReport
    @CompanyID UNIQUEIDENTIFIER,
    @DaysThreshold INT = 90,
    @WarehouseID BIGINT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT 
        p.ProductID, p.ProductCode, p.ProductNameAR,
        sb.WarehouseID, w.WarehouseNameAR,
        sb.AvailableQty, sb.AverageCost, sb.AvailableQty * ISNULL(sb.AverageCost, 0) AS TotalValue,
        DATEDIFF(DAY, ISNULL(MAX(m.MovementDate), GETDATE()), GETDATE()) AS DaysSinceLastMovement,
        COUNT(m.MovementID) AS TotalMovements
    FROM dbo.StockBalances sb
    INNER JOIN dbo.Products p ON sb.ProductID = p.ProductID
    INNER JOIN dbo.Warehouses w ON sb.WarehouseID = w.WarehouseID
    LEFT JOIN dbo.StockMovements m ON p.ProductID = m.ProductID AND m.IsDeleted = 0
    WHERE sb.CompanyID = @CompanyID AND sb.IsDeleted = 0 AND sb.AvailableQty > 0
      AND (@WarehouseID IS NULL OR sb.WarehouseID = @WarehouseID)
    GROUP BY p.ProductID, p.ProductCode, p.ProductNameAR, sb.WarehouseID, w.WarehouseNameAR, sb.AvailableQty, sb.AverageCost
    HAVING DATEDIFF(DAY, ISNULL(MAX(m.MovementDate), GETDATE()), GETDATE()) >= @DaysThreshold
    ORDER BY DaysSinceLastMovement DESC;
END;
GO
PRINT N'✅ [7.5] usp_Stock_SlowMovingReport created.';

IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Stock_CheckExpiry')
    DROP PROCEDURE dbo.usp_Stock_CheckExpiry;
GO
CREATE PROCEDURE dbo.usp_Stock_CheckExpiry
    @CompanyID UNIQUEIDENTIFIER,
    @DaysThreshold INT = 30
AS
BEGIN
    SET NOCOUNT ON;
    SELECT 
        b.ProductID, p.ProductNameAR,
        b.BatchNumber, b.ExpiryDate,
        DATEDIFF(DAY, GETDATE(), b.ExpiryDate) AS DaysRemaining,
        b.CurrentQuantity,
        w.WarehouseNameAR
    FROM dbo.Batches b
    INNER JOIN dbo.Products p ON b.ProductID = p.ProductID
    INNER JOIN dbo.Warehouses w ON b.WarehouseID = w.WarehouseID
    WHERE b.CompanyID = @CompanyID
        AND b.ExpiryDate IS NOT NULL AND b.CurrentQuantity > 0
        AND b.IsDeleted = 0 AND b.IsActive = 1
        AND DATEDIFF(DAY, GETDATE(), b.ExpiryDate) <= @DaysThreshold
    ORDER BY b.ExpiryDate;
END;
GO
PRINT N'✅ [7.6] usp_Stock_CheckExpiry created.';

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
    DECLARE @TotalCost DECIMAL(18,6) = 0, @TotalQty DECIMAL(18,4) = 0;
    SELECT @TotalCost = SUM(RemainingQuantity * UnitCost), @TotalQty = SUM(RemainingQuantity)
    FROM dbo.FIFOLayers
    WHERE CompanyID = @CompanyID AND ProductID = @ProductID 
      AND WarehouseID = @WarehouseID AND IsConsumed = 0 AND IsDeleted = 0;
    
    IF ISNULL(@TotalQty, 0) > 0
        UPDATE dbo.StockBalances
        SET AverageCost = @TotalCost / @TotalQty
        WHERE CompanyID = @CompanyID AND ProductID = @ProductID 
          AND WarehouseID = @WarehouseID AND IsDeleted = 0;
END;
GO
PRINT N'✅ [7.7] usp_Valuation_CalculateAVG created.';


-- ========================================================================
-- ========================================================================

IF EXISTS (SELECT 1 FROM sys.views WHERE name = 'vw_StockValuation')
    DROP VIEW dbo.vw_StockValuation;
GO
CREATE VIEW dbo.vw_StockValuation
AS
SELECT 
    sb.CompanyID, sb.ProductID, p.ProductCode, p.ProductNameAR,
    sb.WarehouseID, w.WarehouseNameAR,
    sb.LocationID, l.LocationCode,
    sb.AvailableQty, sb.ReservedQty, (sb.AvailableQty - sb.ReservedQty) AS AvailableForSale,
    sb.AverageCost, sb.LastCost,
    sb.AvailableQty * ISNULL(sb.AverageCost, sb.LastCost) AS TotalValue,
    sb.ZATCAComplianceStatus, sb.LastUpdated
FROM dbo.StockBalances sb
INNER JOIN dbo.Products p ON sb.ProductID = p.ProductID
INNER JOIN dbo.Warehouses w ON sb.WarehouseID = w.WarehouseID
LEFT JOIN dbo.WarehouseLocations l ON sb.LocationID = l.LocationID
WHERE sb.IsDeleted = 0;
GO
PRINT N'✅ [8.1] vw_StockValuation created.';

IF EXISTS (SELECT 1 FROM sys.views WHERE name = 'vw_BatchInventory')
    DROP VIEW dbo.vw_BatchInventory;
GO
CREATE VIEW dbo.vw_BatchInventory
AS
SELECT 
    b.BatchID, b.ProductID, p.ProductNameAR,
    b.BatchNumber, b.WarehouseID, w.WarehouseNameAR,
    b.CurrentQuantity, b.ReservedQuantity, (b.CurrentQuantity - b.ReservedQuantity) AS AvailableQuantity,
    b.ManufacturingDate, b.ExpiryDate,
    DATEDIFF(DAY, GETDATE(), b.ExpiryDate) AS DaysUntilExpiry,
    b.PurchaseCost, b.ZATCAComplianceStatus,
    CASE 
        WHEN b.ExpiryDate < GETDATE() THEN N'منتهي الصلاحية'
        WHEN DATEDIFF(DAY, GETDATE(), b.ExpiryDate) <= 30 THEN N'ينتهي خلال 30 يوم'
        ELSE N'صالحة'
    END AS ExpiryStatus
FROM dbo.Batches b
INNER JOIN dbo.Products p ON b.ProductID = p.ProductID
INNER JOIN dbo.Warehouses w ON b.WarehouseID = w.WarehouseID
WHERE b.IsDeleted = 0 AND b.CurrentQuantity > 0;
GO
PRINT N'✅ [8.2] vw_BatchInventory created.';

IF EXISTS (SELECT 1 FROM sys.views WHERE name = 'vw_ActiveReservations')
    DROP VIEW dbo.vw_ActiveReservations;
GO
CREATE VIEW dbo.vw_ActiveReservations
AS
SELECT 
    r.ReservationID, r.ReferenceTable, r.ReferenceNumber, r.ReferenceID,
    p.ProductNameAR,
    r.ReservedQuantity, r.ReleasedQuantity, (r.ReservedQuantity - r.ReleasedQuantity) AS OutstandingQuantity,
    r.ReservedAt, r.ExpiryDate, r.Status,
    DATEDIFF(DAY, GETDATE(), r.ExpiryDate) AS DaysUntilExpiry
FROM dbo.StockReservations r
INNER JOIN dbo.StockBalances sb ON r.StockBalanceID = sb.StockBalanceID
INNER JOIN dbo.Products p ON sb.ProductID = p.ProductID
WHERE r.Status IN ('ACTIVE', 'PARTIAL') AND r.IsDeleted = 0;
GO
PRINT N'✅ [8.3] vw_ActiveReservations created.';

-- ========================================================================
-- ========================================================================
    COMMIT TRANSACTION;
    PRINT N'═══════════════════════════════════════════════════════════════════════';
    PRINT N'✅ ULTIMATE STOCK MANAGEMENT (10.10.0) DEPLOYED SUCCESSFULLY.';
    PRINT N'📌 يحتوي هذا الملف على جميع الجداول والإجراءات اللازمة:';
    PRINT N'   - StockBalances, Batches, SerialNumbers, FIFOLayers.';
    PRINT N'   - StockReservations, StockAdjustments, StockAdjustmentItems.';
    PRINT N'   - 7 إجراءات مخزنة (مع FIFO مدمج).';
    PRINT N'   - 3 طرق عرض للتقارير.';
    PRINT N'🏆 هذا هو الإصدار الكامل 10/10 ULTIMATE.';
    PRINT N'═══════════════════════════════════════════════════════════════════════';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    DECLARE @ErrorMessage NVARCHAR(4000) = ERROR_MESSAGE();
    DECLARE @ErrorSeverity INT = ERROR_SEVERITY();
    RAISERROR(@ErrorMessage, @ErrorSeverity, 1);
END CATCH
GO