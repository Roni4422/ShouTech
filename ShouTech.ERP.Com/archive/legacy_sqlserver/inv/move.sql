-- ========================================================================
-- FILE: dba/inv/move_ultimate.sql
-- PROJECT: SHOUTECH ERP V10 - ULTIMATE STOCK MOVEMENTS (10.10.0)
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
PRINT N'SHOUTECH ERP V10 – ULTIMATE STOCK MOVEMENTS (10.10.0)';
PRINT N'═══════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

-- ========================================================================
-- ========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'StockMovements')
    BEGIN
        CREATE TABLE dbo.StockMovements (
            MovementID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            MovementType NVARCHAR(30) NOT NULL,
            MovementDate DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            
            WarehouseIDFrom BIGINT NULL,
            WarehouseIDTo BIGINT NULL,
            LocationIDFrom BIGINT NULL,
            LocationIDTo BIGINT NULL,
            
            StockBalanceID BIGINT NULL,
            
            ProductID INT NOT NULL,
            Quantity DECIMAL(18,4) NOT NULL,
            UnitCost DECIMAL(18,6) NULL,
            TotalCost DECIMAL(18,6) NULL,
            
            BatchID BIGINT NULL,
            BatchNumber NVARCHAR(50) NULL,
            SerialNumberID BIGINT NULL,
            SerialNumber NVARCHAR(100) NULL,
            
            BalanceBefore DECIMAL(18,4) NULL,
            BalanceAfter DECIMAL(18,4) NULL,
            
            ReferenceTable NVARCHAR(50) NULL,
            ReferenceID BIGINT NULL,
            ReferenceLineID BIGINT NULL,
            ReferenceNumber NVARCHAR(50) NULL,
            
            ParentMovementID BIGINT NULL,
            IsReversal BIT NOT NULL DEFAULT 0,
            ReversalReason NVARCHAR(200) NULL,
            
            ZATCAComplianceStatus NVARCHAR(20) NULL DEFAULT 'PENDING',
            TaxNumber NVARCHAR(100) NULL,
            
            Notes NVARCHAR(MAX) NULL,
            
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            
            CONSTRAINT PK_StockMovements PRIMARY KEY CLUSTERED (MovementID),
            CONSTRAINT FK_StockMovements_Product FOREIGN KEY (ProductID) 
                REFERENCES dbo.Products(ProductID),
            CONSTRAINT FK_StockMovements_Batch FOREIGN KEY (BatchID) 
                REFERENCES dbo.Batches(BatchID),
            CONSTRAINT FK_StockMovements_Serial FOREIGN KEY (SerialNumberID) 
                REFERENCES dbo.SerialNumbers(SerialID),
            CONSTRAINT FK_StockMovements_StockBalance FOREIGN KEY (StockBalanceID) 
                REFERENCES dbo.StockBalances(StockBalanceID),
            CONSTRAINT FK_StockMovements_FromWarehouse FOREIGN KEY (WarehouseIDFrom) 
                REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT FK_StockMovements_ToWarehouse FOREIGN KEY (WarehouseIDTo) 
                REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT FK_StockMovements_FromLocation FOREIGN KEY (LocationIDFrom) 
                REFERENCES dbo.WarehouseLocations(LocationID),
            CONSTRAINT FK_StockMovements_ToLocation FOREIGN KEY (LocationIDTo) 
                REFERENCES dbo.WarehouseLocations(LocationID),
            CONSTRAINT FK_StockMovements_Parent FOREIGN KEY (ParentMovementID) 
                REFERENCES dbo.StockMovements(MovementID),
            CONSTRAINT CK_StockMovements_Type CHECK (MovementType IN 
                ('PURCHASE', 'SALE', 'RETURN_PURCHASE', 'RETURN_SALE', 
                 'TRANSFER_IN', 'TRANSFER_OUT', 'ADJUSTMENT', 
                 'COUNT_OPEN', 'COUNT_CLOSE', 'REVERSAL')),
            CONSTRAINT CK_StockMovements_ZATCA CHECK (ZATCAComplianceStatus IN 
                ('PENDING', 'VERIFIED', 'REJECTED', NULL))
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ القسم 1: StockMovements table created (Ultimate with full tracking).';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_StockMovements_Product 
        ON dbo.StockMovements(ProductID, MovementDate DESC) WHERE IsDeleted = 0;

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_StockMovements_Reference 
        ON dbo.StockMovements(ReferenceTable, ReferenceID) WHERE IsDeleted = 0;

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_StockMovements_BatchSerial 
        ON dbo.StockMovements(BatchID, SerialNumberID) WHERE IsDeleted = 0;

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_StockMovements_StockBalance 
        ON dbo.StockMovements(StockBalanceID, MovementDate DESC) WHERE IsDeleted = 0;

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_StockMovements_Parent 
        ON dbo.StockMovements(ParentMovementID) WHERE ParentMovementID IS NOT NULL AND IsDeleted = 0;

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_StockMovements_ZATCA 
        ON dbo.StockMovements(ZATCAComplianceStatus) WHERE ZATCAComplianceStatus IS NOT NULL AND IsDeleted = 0;

    CREATE NONCLUSTERED COLUMNSTORE INDEX IF NOT EXISTS CSIX_StockMovements_Analytics 
        ON dbo.StockMovements (CompanyID, MovementType, MovementDate, ProductID, Quantity, TotalCost, ZATCAComplianceStatus);
    GO

-- ========================================================================
-- ========================================================================

IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Stock_AddMovement')
    DROP PROCEDURE dbo.usp_Stock_AddMovement;
GO
CREATE PROCEDURE dbo.usp_Stock_AddMovement
    @CompanyID UNIQUEIDENTIFIER,
    @MovementType NVARCHAR(30),
    @ProductID INT,
    @Quantity DECIMAL(18,4),
    @UnitCost DECIMAL(18,6) = NULL,
    @WarehouseIDFrom BIGINT = NULL,
    @WarehouseIDTo BIGINT = NULL,
    @LocationIDFrom BIGINT = NULL,
    @LocationIDTo BIGINT = NULL,
    @BatchNumber NVARCHAR(50) = NULL,
    @SerialNumber NVARCHAR(100) = NULL,
    @ExpiryDate DATE = NULL,
    @ReferenceTable NVARCHAR(50) = NULL,
    @ReferenceID BIGINT = NULL,
    @ReferenceLineID BIGINT = NULL,
    @ReferenceNumber NVARCHAR(50) = NULL,
    @ParentMovementID BIGINT = NULL,
    @IsReversal BIT = 0,
    @ReversalReason NVARCHAR(200) = NULL,
    @ZATCAStatus NVARCHAR(20) = 'PENDING',
    @TaxNumber NVARCHAR(100) = NULL,
    @Notes NVARCHAR(MAX) = NULL,
    @CreatedBy UNIQUEIDENTIFIER,
    @NewMovementID BIGINT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        BEGIN TRANSACTION;

        DECLARE @StockBalanceID BIGINT = NULL;
        DECLARE @BalanceBefore DECIMAL(18,4) = NULL;
        DECLARE @BalanceAfter DECIMAL(18,4) = NULL;
        DECLARE @CurrentQty DECIMAL(18,4) = 0;
        DECLARE @EffectiveWarehouseID BIGINT;
        DECLARE @EffectiveLocationID BIGINT;

        IF @MovementType IN ('PURCHASE', 'RETURN_SALE', 'TRANSFER_IN', 'COUNT_OPEN')
        BEGIN
            SET @EffectiveWarehouseID = @WarehouseIDTo;
            SET @EffectiveLocationID = @LocationIDTo;
        END
        ELSE IF @MovementType IN ('SALE', 'RETURN_PURCHASE', 'TRANSFER_OUT', 'COUNT_CLOSE', 'ADJUSTMENT')
        BEGIN
            SET @EffectiveWarehouseID = @WarehouseIDFrom;
            SET @EffectiveLocationID = @LocationIDFrom;
        END

        IF @EffectiveWarehouseID IS NOT NULL
        BEGIN
            SELECT @StockBalanceID = StockBalanceID, @CurrentQty = AvailableQty
            FROM dbo.StockBalances
            WHERE CompanyID = @CompanyID 
                AND WarehouseID = @EffectiveWarehouseID
                AND ISNULL(LocationID, -1) = ISNULL(@EffectiveLocationID, -1)
                AND ProductID = @ProductID
                AND IsDeleted = 0;
        END

        DECLARE @NewBalance DECIMAL(18,4);
        DECLARE @IsIncrease BIT = CASE WHEN @MovementType IN ('PURCHASE', 'RETURN_SALE', 'TRANSFER_IN', 'COUNT_OPEN') THEN 1 ELSE 0 END;

        EXEC dbo.usp_Stock_UpdateBalance
            @CompanyID = @CompanyID,
            @ProductID = @ProductID,
            @WarehouseID = @EffectiveWarehouseID,
            @LocationID = @EffectiveLocationID,
            @Quantity = @Quantity,
            @IsIncrease = @IsIncrease,
            @BatchNumber = @BatchNumber,
            @SerialNumber = @SerialNumber,
            @ExpiryDate = @ExpiryDate,
            @UnitCost = @UnitCost,
            @ReferenceType = @MovementType,
            @ReferenceID = NULL, -- سيتم تحديثه لاحقاً بمعرف الحركة
            @ReferenceLineID = @ReferenceLineID,
            @CreatedBy = @CreatedBy,
            @ZATCAStatus = @ZATCAStatus,
            @TaxNumber = @TaxNumber,
            @NewBalance = @NewBalance OUTPUT;

        IF @StockBalanceID IS NULL AND @EffectiveWarehouseID IS NOT NULL
        BEGIN
            SELECT @StockBalanceID = StockBalanceID
            FROM dbo.StockBalances
            WHERE CompanyID = @CompanyID 
                AND WarehouseID = @EffectiveWarehouseID
                AND ISNULL(LocationID, -1) = ISNULL(@EffectiveLocationID, -1)
                AND ProductID = @ProductID
                AND IsDeleted = 0;
        END

        SET @BalanceBefore = @CurrentQty;
        SET @BalanceAfter = @NewBalance;

        INSERT INTO dbo.StockMovements (
            CompanyID, MovementType, MovementDate,
            WarehouseIDFrom, WarehouseIDTo,
            LocationIDFrom, LocationIDTo,
            StockBalanceID,
            ProductID, Quantity, UnitCost, TotalCost,
            BatchID, BatchNumber,
            SerialNumberID, SerialNumber,
            BalanceBefore, BalanceAfter,
            ReferenceTable, ReferenceID, ReferenceLineID, ReferenceNumber,
            ParentMovementID, IsReversal, ReversalReason,
            ZATCAComplianceStatus, TaxNumber,
            Notes, CreatedBy
        ) VALUES (
            @CompanyID, @MovementType, SYSUTCDATETIME(),
            @WarehouseIDFrom, @WarehouseIDTo,
            @LocationIDFrom, @LocationIDTo,
            @StockBalanceID,
            @ProductID, @Quantity, @UnitCost, @Quantity * ISNULL(@UnitCost, 0),
            (SELECT BatchID FROM dbo.Batches WHERE CompanyID = @CompanyID AND BatchNumber = @BatchNumber AND IsDeleted = 0),
            @BatchNumber,
            (SELECT SerialID FROM dbo.SerialNumbers WHERE CompanyID = @CompanyID AND SerialNumber = @SerialNumber AND IsDeleted = 0),
            @SerialNumber,
            @BalanceBefore, @BalanceAfter,
            @ReferenceTable, @ReferenceID, @ReferenceLineID, @ReferenceNumber,
            @ParentMovementID, @IsReversal, @ReversalReason,
            @ZATCAStatus, @TaxNumber,
            @Notes, @CreatedBy
        );
        SET @NewMovementID = SCOPE_IDENTITY();

        IF @StockBalanceID IS NOT NULL
        BEGIN
            UPDATE dbo.StockBalances
            SET LastTransactionID = @NewMovementID,
                LastTransactionType = @MovementType,
                LastTransactionDate = SYSUTCDATETIME()
            WHERE StockBalanceID = @StockBalanceID;
        END

        UPDATE dbo.StockMovements
        SET ReferenceID = @NewMovementID
        WHERE MovementID = @NewMovementID AND ReferenceID IS NULL;

        IF @BatchNumber IS NOT NULL AND @IsIncrease = 0
        BEGIN
            UPDATE dbo.Batches
            SET LastTransactionID = @NewMovementID,
                LastTransactionDate = SYSUTCDATETIME()
            WHERE CompanyID = @CompanyID AND BatchNumber = @BatchNumber AND IsDeleted = 0;
        END

        IF @SerialNumber IS NOT NULL AND @IsIncrease = 0
        BEGIN
            UPDATE dbo.SerialNumbers
            SET LastTransactionID = @NewMovementID,
                LastTransactionDate = SYSUTCDATETIME()
            WHERE CompanyID = @CompanyID AND SerialNumber = @SerialNumber AND IsDeleted = 0;
        END

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END;
GO
PRINT N'✅ [2.1] usp_Stock_AddMovement created (Integrated with all systems).';


IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Stock_ReverseMovement')
    DROP PROCEDURE dbo.usp_Stock_ReverseMovement;
GO
CREATE PROCEDURE dbo.usp_Stock_ReverseMovement
    @MovementID BIGINT,
    @ReversalReason NVARCHAR(200) = NULL,
    @ReversedBy UNIQUEIDENTIFIER,
    @NewReversalID BIGINT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        BEGIN TRANSACTION;

        DECLARE @IsReversed BIT;
        SELECT @IsReversed = IsReversal FROM dbo.StockMovements WHERE MovementID = @MovementID AND IsDeleted = 0;
        IF @IsReversed IS NULL THROW 50000, 'الحركة غير موجودة.', 1;
        IF @IsReversed = 1 THROW 50000, 'لا يمكن التراجع عن حركة تراجع.', 1;

        DECLARE @CompanyID UNIQUEIDENTIFIER,
                @ProductID INT,
                @Quantity DECIMAL(18,4),
                @MovementType NVARCHAR(30),
                @UnitCost DECIMAL(18,6),
                @WarehouseIDFrom BIGINT,
                @WarehouseIDTo BIGINT,
                @LocationIDFrom BIGINT,
                @LocationIDTo BIGINT,
                @BatchNumber NVARCHAR(50),
                @SerialNumber NVARCHAR(100),
                @ZATCAStatus NVARCHAR(20),
                @TaxNumber NVARCHAR(100);

        SELECT 
            @CompanyID = CompanyID,
            @ProductID = ProductID,
            @Quantity = Quantity,
            @MovementType = MovementType,
            @UnitCost = UnitCost,
            @WarehouseIDFrom = WarehouseIDFrom,
            @WarehouseIDTo = WarehouseIDTo,
            @LocationIDFrom = LocationIDFrom,
            @LocationIDTo = LocationIDTo,
            @BatchNumber = BatchNumber,
            @SerialNumber = SerialNumber,
            @ZATCAStatus = ZATCAComplianceStatus,
            @TaxNumber = TaxNumber
        FROM dbo.StockMovements
        WHERE MovementID = @MovementID AND IsDeleted = 0;

        IF @CompanyID IS NULL THROW 50000, 'فشل في جلب بيانات الحركة.', 1;

        DECLARE @ReverseType NVARCHAR(30);
        SET @ReverseType = CASE 
            WHEN @MovementType = 'PURCHASE' THEN 'RETURN_PURCHASE'
            WHEN @MovementType = 'SALE' THEN 'RETURN_SALE'
            WHEN @MovementType = 'RETURN_PURCHASE' THEN 'PURCHASE'
            WHEN @MovementType = 'RETURN_SALE' THEN 'SALE'
            WHEN @MovementType = 'TRANSFER_IN' THEN 'TRANSFER_OUT'
            WHEN @MovementType = 'TRANSFER_OUT' THEN 'TRANSFER_IN'
            WHEN @MovementType = 'ADJUSTMENT' THEN 'ADJUSTMENT' -- التسوية العكسية بنفس النوع (مع إشارة معاكسة)
            ELSE 'REVERSAL'
        END;

        DECLARE @RevWarehouseFrom BIGINT = @WarehouseIDTo;
        DECLARE @RevWarehouseTo BIGINT = @WarehouseIDFrom;
        DECLARE @RevLocationFrom BIGINT = @LocationIDTo;
        DECLARE @RevLocationTo BIGINT = @LocationIDFrom;

        EXEC dbo.usp_Stock_AddMovement
            @CompanyID = @CompanyID,
            @MovementType = @ReverseType,
            @ProductID = @ProductID,
            @Quantity = @Quantity,
            @UnitCost = @UnitCost,
            @WarehouseIDFrom = @RevWarehouseFrom,
            @WarehouseIDTo = @RevWarehouseTo,
            @LocationIDFrom = @RevLocationFrom,
            @LocationIDTo = @RevLocationTo,
            @BatchNumber = @BatchNumber,
            @SerialNumber = @SerialNumber,
            @ExpiryDate = NULL,
            @ReferenceTable = 'StockMovements',
            @ReferenceID = @MovementID,
            @ReferenceNumber = NULL,
            @ParentMovementID = @MovementID,
            @IsReversal = 1,
            @ReversalReason = @ReversalReason,
            @ZATCAStatus = @ZATCAStatus,
            @TaxNumber = @TaxNumber,
            @Notes = N'حركة تراجع عن الحركة رقم ' + CAST(@MovementID AS NVARCHAR),
            @CreatedBy = @ReversedBy,
            @NewMovementID = @NewReversalID OUTPUT;

        UPDATE dbo.StockMovements
        SET IsReversal = 1,
            ReversalReason = @ReversalReason,
            UpdatedBy = @ReversedBy,
            UpdatedAt = SYSUTCDATETIME()
        WHERE MovementID = @MovementID;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END;
GO
PRINT N'✅ [2.2] usp_Stock_ReverseMovement created (Reversal with audit).';


IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Stock_GetProductMovements')
    DROP PROCEDURE dbo.usp_Stock_GetProductMovements;
GO
CREATE PROCEDURE dbo.usp_Stock_GetProductMovements
    @CompanyID UNIQUEIDENTIFIER,
    @ProductID INT,
    @FromDate DATE = NULL,
    @ToDate DATE = NULL,
    @WarehouseID BIGINT = NULL,
    @BatchNumber NVARCHAR(50) = NULL,
    @SerialNumber NVARCHAR(100) = NULL,
    @ZATCAStatus NVARCHAR(20) = NULL,
    @IncludeReversals BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    IF @FromDate IS NULL SET @FromDate = DATEADD(MONTH, -3, GETDATE());
    IF @ToDate IS NULL SET @ToDate = GETDATE();

    SELECT 
        m.MovementID,
        m.MovementType,
        m.MovementDate,
        m.Quantity,
        m.UnitCost,
        m.TotalCost,
        m.BalanceBefore,
        m.BalanceAfter,
        m.ReferenceNumber,
        m.ReferenceTable,
        m.BatchNumber,
        m.SerialNumber,
        m.ZATCAComplianceStatus,
        m.TaxNumber,
        m.IsReversal,
        m.ParentMovementID,
        wf.WarehouseNameAR AS FromWarehouse,
        wt.WarehouseNameAR AS ToWarehouse,
        lf.LocationCode AS FromLocation,
        lt.LocationCode AS ToLocation,
        m.Notes,
        u.UserName AS CreatedByUser,
        CASE 
            WHEN m.IsReversal = 1 THEN N'حركة تراجع'
            WHEN EXISTS (SELECT 1 FROM dbo.StockMovements p WHERE p.ParentMovementID = m.MovementID AND p.IsReversal = 1 AND p.IsDeleted = 0) 
                THEN N'تم التراجع عنها'
            ELSE N'نشطة'
        END AS MovementStatus
    FROM dbo.StockMovements m
    LEFT JOIN dbo.Warehouses wf ON m.WarehouseIDFrom = wf.WarehouseID
    LEFT JOIN dbo.Warehouses wt ON m.WarehouseIDTo = wt.WarehouseID
    LEFT JOIN dbo.WarehouseLocations lf ON m.LocationIDFrom = lf.LocationID
    LEFT JOIN dbo.WarehouseLocations lt ON m.LocationIDTo = lt.LocationID
    LEFT JOIN dbo.Users u ON m.CreatedBy = u.UserID
    WHERE m.CompanyID = @CompanyID
        AND m.ProductID = @ProductID
        AND m.IsDeleted = 0
        AND (@IncludeReversals = 1 OR m.IsReversal = 0)
        AND (@WarehouseID IS NULL OR m.WarehouseIDFrom = @WarehouseID OR m.WarehouseIDTo = @WarehouseID)
        AND (@BatchNumber IS NULL OR m.BatchNumber = @BatchNumber)
        AND (@SerialNumber IS NULL OR m.SerialNumber = @SerialNumber)
        AND (@ZATCAStatus IS NULL OR m.ZATCAComplianceStatus = @ZATCAStatus)
        AND CAST(m.MovementDate AS DATE) >= @FromDate
        AND CAST(m.MovementDate AS DATE) <= @ToDate
    ORDER BY m.MovementDate DESC;
END;
GO
PRINT N'✅ [2.3] usp_Stock_GetProductMovements created (Advanced filtering).';


IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Stock_GetWarehouseMovements')
    DROP PROCEDURE dbo.usp_Stock_GetWarehouseMovements;
GO
CREATE PROCEDURE dbo.usp_Stock_GetWarehouseMovements
    @CompanyID UNIQUEIDENTIFIER,
    @WarehouseID BIGINT,
    @FromDate DATE = NULL,
    @ToDate DATE = NULL,
    @MovementType NVARCHAR(30) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @FromDate IS NULL SET @FromDate = DATEADD(MONTH, -1, GETDATE());
    IF @ToDate IS NULL SET @ToDate = GETDATE();

    SELECT 
        m.MovementID,
        m.MovementType,
        m.MovementDate,
        p.ProductCode,
        p.ProductNameAR,
        m.Quantity,
        m.UnitCost,
        m.TotalCost,
        m.BatchNumber,
        m.SerialNumber,
        m.ReferenceNumber,
        m.ZATCAComplianceStatus,
        m.IsReversal,
        u.UserName AS CreatedByUser
    FROM dbo.StockMovements m
    INNER JOIN dbo.Products p ON m.ProductID = p.ProductID
    LEFT JOIN dbo.Users u ON m.CreatedBy = u.UserID
    WHERE m.CompanyID = @CompanyID
        AND (m.WarehouseIDFrom = @WarehouseID OR m.WarehouseIDTo = @WarehouseID)
        AND m.IsDeleted = 0
        AND (@MovementType IS NULL OR m.MovementType = @MovementType)
        AND CAST(m.MovementDate AS DATE) >= @FromDate
        AND CAST(m.MovementDate AS DATE) <= @ToDate
    ORDER BY m.MovementDate DESC;
END;
GO
PRINT N'✅ [2.4] usp_Stock_GetWarehouseMovements created (Warehouse report).';


IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Stock_GetZATCAMovements')
    DROP PROCEDURE dbo.usp_Stock_GetZATCAMovements;
GO
CREATE PROCEDURE dbo.usp_Stock_GetZATCAMovements
    @CompanyID UNIQUEIDENTIFIER,
    @FromDate DATE = NULL,
    @ToDate DATE = NULL,
    @ZATCAStatus NVARCHAR(20) = 'PENDING'
AS
BEGIN
    SET NOCOUNT ON;
    IF @FromDate IS NULL SET @FromDate = DATEADD(DAY, -30, GETDATE());
    IF @ToDate IS NULL SET @ToDate = GETDATE();

    SELECT 
        m.MovementID,
        m.MovementType,
        m.MovementDate,
        p.ProductCode,
        p.ProductNameAR,
        m.Quantity,
        m.UnitCost,
        m.TotalCost,
        m.BatchNumber,
        m.SerialNumber,
        m.TaxNumber,
        m.ZATCAComplianceStatus,
        m.ReferenceNumber,
        m.Notes
    FROM dbo.StockMovements m
    INNER JOIN dbo.Products p ON m.ProductID = p.ProductID
    WHERE m.CompanyID = @CompanyID
        AND m.IsDeleted = 0
        AND (@ZATCAStatus IS NULL OR m.ZATCAComplianceStatus = @ZATCAStatus)
        AND CAST(m.MovementDate AS DATE) >= @FromDate
        AND CAST(m.MovementDate AS DATE) <= @ToDate
    ORDER BY m.MovementDate DESC;
END;
GO
PRINT N'✅ [2.5] usp_Stock_GetZATCAMovements created (Tax compliance report).';

-- ========================================================================
-- ========================================================================

IF EXISTS (SELECT 1 FROM sys.views WHERE name = 'vw_DailyMovements')
    DROP VIEW dbo.vw_DailyMovements;
GO
CREATE VIEW dbo.vw_DailyMovements
AS
SELECT 
    m.MovementID,
    m.MovementType,
    m.MovementDate,
    p.ProductCode,
    p.ProductNameAR,
    m.Quantity,
    m.UnitCost,
    m.TotalCost,
    m.BatchNumber,
    m.SerialNumber,
    m.ReferenceNumber,
    m.ZATCAComplianceStatus,
    m.IsReversal,
    u.UserName AS CreatedByUser,
    wf.WarehouseNameAR AS FromWarehouse,
    wt.WarehouseNameAR AS ToWarehouse
FROM dbo.StockMovements m
INNER JOIN dbo.Products p ON m.ProductID = p.ProductID
LEFT JOIN dbo.Warehouses wf ON m.WarehouseIDFrom = wf.WarehouseID
LEFT JOIN dbo.Warehouses wt ON m.WarehouseIDTo = wt.WarehouseID
LEFT JOIN dbo.Users u ON m.CreatedBy = u.UserID
WHERE m.IsDeleted = 0
  AND CAST(m.MovementDate AS DATE) = CAST(GETDATE() AS DATE);
GO
PRINT N'✅ [3.1] vw_DailyMovements created.';

IF EXISTS (SELECT 1 FROM sys.views WHERE name = 'vw_ReversedMovements')
    DROP VIEW dbo.vw_ReversedMovements;
GO
CREATE VIEW dbo.vw_ReversedMovements
AS
SELECT 
    m.MovementID,
    m.MovementType,
    m.MovementDate,
    p.ProductCode,
    p.ProductNameAR,
    m.Quantity,
    m.UnitCost,
    m.TotalCost,
    m.ReversalReason,
    m.ParentMovementID,
    m.ReferenceNumber,
    u.UserName AS ReversedByUser,
    m.UpdatedAt AS ReversedAt
FROM dbo.StockMovements m
INNER JOIN dbo.Products p ON m.ProductID = p.ProductID
LEFT JOIN dbo.Users u ON m.UpdatedBy = u.UserID
WHERE m.IsReversal = 1 AND m.IsDeleted = 0;
GO
'═══════════════════════════════════════════════════════════════════════';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    DECLARE @ErrorMessage NVARCHAR(4000) = ERROR_MESSAGE();
    DECLARE @ErrorSeverity INT = ERROR_SEVERITY();
    RAISERROR(@ErrorMessage, @ErrorSeverity, 1);
END CATCH
GO