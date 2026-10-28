-- ========================================================================
-- FILE: dba/inv/val_ultimate.sql
-- PROJECT: SHOUTECH ERP V10 - ULTIMATE INVENTORY VALUATION (10.10.0)
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
PRINT N'SHOUTECH ERP V10 – ULTIMATE INVENTORY VALUATION (10.10.0)';
PRINT N'═══════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

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
            MethodDescription NVARCHAR(500) NULL,
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
        PRINT N'✅ [1] ValuationMethods table created.';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ValuationMethods_Company_Active 
        ON dbo.ValuationMethods(CompanyID, IsActive) WHERE IsDeleted = 0;
    GO

-- ========================================================================
-- ========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ProductValuation')
    BEGIN
        CREATE TABLE dbo.ProductValuation (
            ProductValuationID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ProductID INT NOT NULL,
            ValuationMethodID INT NOT NULL,
            EffectiveDate DATE NOT NULL DEFAULT CAST(GETDATE() AS DATE),
            IsActive BIT NOT NULL DEFAULT 1,
            Notes NVARCHAR(500) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_ProductValuation PRIMARY KEY CLUSTERED (ProductValuationID),
            CONSTRAINT UQ_ProductValuation_Product UNIQUE (CompanyID, ProductID, EffectiveDate),
            CONSTRAINT FK_ProductValuation_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID),
            CONSTRAINT FK_ProductValuation_Method FOREIGN KEY (ValuationMethodID) REFERENCES dbo.ValuationMethods(ValuationMethodID),
            CONSTRAINT FK_ProductValuation_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ [2] ProductValuation table created (with EffectiveDate).';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ProductValuation_Product_Effective 
        ON dbo.ProductValuation(ProductID, EffectiveDate DESC) WHERE IsDeleted = 0;
    GO

-- ========================================================================
-- ========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CostLayers')
    BEGIN
        CREATE TABLE dbo.CostLayers (
            LayerID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ProductID INT NOT NULL,
            WarehouseID BIGINT NOT NULL,
            BatchID BIGINT NULL,
            LayerDate DATE NOT NULL,
            LayerSequence INT NOT NULL DEFAULT 0,  -- 0=FIFO, 1=LIFO (لتسهيل الاستهلاك)
            Quantity DECIMAL(18,4) NOT NULL,
            UnitCost DECIMAL(18,6) NOT NULL,
            RemainingQuantity DECIMAL(18,4) NOT NULL,
            CurrencyCode NVARCHAR(3) DEFAULT 'SAR',
            SourceReference NVARCHAR(50) NULL,
            SourceID BIGINT NULL,
            SourceMovementID BIGINT NULL,          -- ربط مباشر بـ StockMovements
            IsConsumed BIT NOT NULL DEFAULT 0,
            ConsumedAt DATETIME2(7) NULL,
            ConsumedBy UNIQUEIDENTIFIER NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_CostLayers PRIMARY KEY CLUSTERED (LayerID),
            CONSTRAINT FK_CostLayers_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID),
            CONSTRAINT FK_CostLayers_Warehouse FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT FK_CostLayers_Batch FOREIGN KEY (BatchID) REFERENCES dbo.Batches(BatchID),
            CONSTRAINT FK_CostLayers_Movement FOREIGN KEY (SourceMovementID) REFERENCES dbo.StockMovements(MovementID)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ [3] CostLayers table created (Support FIFO & LIFO).';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_CostLayers_Product_Warehouse_FIFO 
        ON dbo.CostLayers(ProductID, WarehouseID, LayerDate, LayerSequence) 
        INCLUDE (RemainingQuantity, UnitCost) 
        WHERE IsConsumed = 0 AND IsDeleted = 0;

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_CostLayers_Product_Warehouse_LIFO 
        ON dbo.CostLayers(ProductID, WarehouseID, LayerDate DESC, LayerSequence DESC) 
        INCLUDE (RemainingQuantity, UnitCost) 
        WHERE IsConsumed = 0 AND IsDeleted = 0;

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_CostLayers_Movement 
        ON dbo.CostLayers(SourceMovementID) WHERE SourceMovementID IS NOT NULL AND IsDeleted = 0;

    CREATE NONCLUSTERED COLUMNSTORE INDEX IF NOT EXISTS CSIX_CostLayers_Analytics 
        ON dbo.CostLayers (ProductID, WarehouseID, LayerDate, Quantity, UnitCost, RemainingQuantity);
    GO

-- ========================================================================
-- ========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ValuationHistory')
    BEGIN
        CREATE TABLE dbo.ValuationHistory (
            ValuationHistoryID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ProductID INT NOT NULL,
            WarehouseID BIGINT NOT NULL,
            ValuationDate DATE NOT NULL,
            ValuationMethodID INT NOT NULL,
            TotalQuantity DECIMAL(18,4) NOT NULL,
            TotalValue DECIMAL(18,6) NOT NULL,
            AverageCost DECIMAL(18,6) NOT NULL,
            CurrencyCode NVARCHAR(3) DEFAULT 'SAR',
            Notes NVARCHAR(500) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            RowVersion ROWVERSION,
            CONSTRAINT PK_ValuationHistory PRIMARY KEY CLUSTERED (ValuationHistoryID),
            CONSTRAINT FK_ValuationHistory_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID),
            CONSTRAINT FK_ValuationHistory_Warehouse FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT FK_ValuationHistory_Method FOREIGN KEY (ValuationMethodID) REFERENCES dbo.ValuationMethods(ValuationMethodID)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ [4] ValuationHistory table created (Historical tracking).';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ValuationHistory_Product_Date 
        ON dbo.ValuationHistory(ProductID, ValuationDate DESC) WHERE IsDeleted = 0;

    CREATE NONCLUSTERED COLUMNSTORE INDEX IF NOT EXISTS CSIX_ValuationHistory_Analytics 
        ON dbo.ValuationHistory (ProductID, WarehouseID, ValuationDate, TotalQuantity, TotalValue);
    GO

-- ========================================================================
-- ========================================================================

IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Valuation_CalculateAVG')
    DROP PROCEDURE dbo.usp_Valuation_CalculateAVG;
GO
CREATE PROCEDURE dbo.usp_Valuation_CalculateAVG
    @CompanyID UNIQUEIDENTIFIER,
    @ProductID INT,
    @WarehouseID BIGINT,
    @AsOfDate DATE = NULL,
    @CalculateForAllProducts BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOfDate IS NULL SET @AsOfDate = GETDATE();

    IF @CalculateForAllProducts = 1
    BEGIN
        DECLARE @CurrentProductID INT;
        DECLARE cur CURSOR FOR
            SELECT DISTINCT ProductID 
            FROM dbo.StockBalances 
            WHERE CompanyID = @CompanyID 
                AND IsDeleted = 0 
                AND AvailableQty > 0
                AND (@WarehouseID IS NULL OR WarehouseID = @WarehouseID);
        
        OPEN cur;
        FETCH NEXT FROM cur INTO @CurrentProductID;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            EXEC dbo.usp_Valuation_CalculateAVG @CompanyID, @CurrentProductID, @WarehouseID, @AsOfDate, 0;
            FETCH NEXT FROM cur INTO @CurrentProductID;
        END
        CLOSE cur; DEALLOCATE cur;
        RETURN;
    END

    DECLARE @TotalCost DECIMAL(18,6) = 0;
    DECLARE @TotalQty DECIMAL(18,4) = 0;
    DECLARE @AvgCost DECIMAL(18,6) = 0;

    SELECT @TotalCost = SUM(RemainingQuantity * UnitCost),
           @TotalQty = SUM(RemainingQuantity)
    FROM dbo.CostLayers
    WHERE CompanyID = @CompanyID
        AND ProductID = @ProductID
        AND (@WarehouseID IS NULL OR WarehouseID = @WarehouseID)
        AND IsConsumed = 0 AND IsDeleted = 0;

    IF @TotalQty > 0
        SET @AvgCost = @TotalCost / @TotalQty;

    UPDATE dbo.StockBalances 
    SET AverageCost = @AvgCost
    WHERE CompanyID = @CompanyID
        AND ProductID = @ProductID
        AND (@WarehouseID IS NULL OR WarehouseID = @WarehouseID)
        AND IsDeleted = 0;

    SELECT @AvgCost AS AverageCost, @TotalQty AS TotalQuantity;
END;
GO
PRINT N'✅ [5.1] usp_Valuation_CalculateAVG created (Enhanced).';


IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Valuation_GetLayerCost')
    DROP PROCEDURE dbo.usp_Valuation_GetLayerCost;
GO
CREATE PROCEDURE dbo.usp_Valuation_GetLayerCost
    @CompanyID UNIQUEIDENTIFIER,
    @ProductID INT,
    @WarehouseID BIGINT,
    @Quantity DECIMAL(18,4),
    @MethodCode NVARCHAR(20) = 'FIFO',  -- FIFO, LIFO
    @AsOfDate DATE = NULL,
    @TotalCost DECIMAL(18,6) OUTPUT,
    @UsedLayers NVARCHAR(MAX) = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOfDate IS NULL SET @AsOfDate = GETDATE();

    DECLARE @RemainingQty DECIMAL(18,4) = @Quantity;
    DECLARE @LayerQty DECIMAL(18,4), @LayerUnitCost DECIMAL(18,6), @LayerID BIGINT;
    DECLARE @LayerDetails TABLE (LayerID BIGINT, ConsumedQty DECIMAL(18,4), UnitCost DECIMAL(18,6));
    
    SET @TotalCost = 0;
    DECLARE @LayerType NVARCHAR(10) = CASE WHEN @MethodCode = 'LIFO' THEN 'DESC' ELSE 'ASC' END;
    
    DECLARE cur CURSOR FOR
        SELECT LayerID, RemainingQuantity, UnitCost
        FROM dbo.CostLayers
        WHERE CompanyID = @CompanyID
            AND ProductID = @ProductID
            AND WarehouseID = @WarehouseID
            AND IsConsumed = 0 AND IsDeleted = 0
            AND LayerDate <= @AsOfDate
        ORDER BY 
            CASE WHEN @MethodCode = 'LIFO' THEN LayerDate END DESC,
            CASE WHEN @MethodCode = 'FIFO' THEN LayerDate END ASC,
            LayerSequence;

    OPEN cur;
    FETCH NEXT FROM cur INTO @LayerID, @LayerQty, @LayerUnitCost;
    
    WHILE @@FETCH_STATUS = 0 AND @RemainingQty > 0
    BEGIN
        IF @LayerQty >= @RemainingQty
        BEGIN
            SET @TotalCost = @TotalCost + (@RemainingQty * @LayerUnitCost);
            INSERT INTO @LayerDetails VALUES (@LayerID, @RemainingQty, @LayerUnitCost);
            
            UPDATE dbo.CostLayers 
            SET RemainingQuantity = RemainingQuantity - @RemainingQty,
                IsConsumed = CASE WHEN RemainingQuantity - @RemainingQty = 0 THEN 1 ELSE 0 END,
                ConsumedAt = CASE WHEN RemainingQuantity - @RemainingQty = 0 THEN SYSUTCDATETIME() ELSE NULL END
            WHERE LayerID = @LayerID;
            SET @RemainingQty = 0;
        END
        ELSE
        BEGIN
            SET @TotalCost = @TotalCost + (@LayerQty * @LayerUnitCost);
            INSERT INTO @LayerDetails VALUES (@LayerID, @LayerQty, @LayerUnitCost);
            SET @RemainingQty = @RemainingQty - @LayerQty;
            
            UPDATE dbo.CostLayers 
            SET RemainingQuantity = 0,
                IsConsumed = 1,
                ConsumedAt = SYSUTCDATETIME()
            WHERE LayerID = @LayerID;
        END
        FETCH NEXT FROM cur INTO @LayerID, @LayerQty, @LayerUnitCost;
    END
    
    CLOSE cur; DEALLOCATE cur;
    
    IF @RemainingQty > 0
        THROW 50000, N'الكمية المطلوبة غير متوفرة في طبقات التكلفة.', 1;
    
    SELECT @UsedLayers = STRING_AGG(
        CAST(LayerID AS NVARCHAR) + ':' + CAST(ConsumedQty AS NVARCHAR) + '@' + CAST(UnitCost AS NVARCHAR),
        ';'
    ) FROM @LayerDetails;
    
    SELECT @TotalCost AS TotalCost;
END;
GO
PRINT N'✅ [5.2] usp_Valuation_GetLayerCost created (FIFO/LIFO).';


IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Valuation_AddLayer')
    DROP PROCEDURE dbo.usp_Valuation_AddLayer;
GO
CREATE PROCEDURE dbo.usp_Valuation_AddLayer
    @CompanyID UNIQUEIDENTIFIER,
    @ProductID INT,
    @WarehouseID BIGINT,
    @Quantity DECIMAL(18,4),
    @UnitCost DECIMAL(18,6),
    @BatchID BIGINT = NULL,
    @LayerDate DATE = NULL,
    @SourceMovementID BIGINT = NULL,
    @SourceReference NVARCHAR(50) = NULL,
    @SourceID BIGINT = NULL,
    @CurrencyCode NVARCHAR(3) = 'SAR',
    @CreatedBy UNIQUEIDENTIFIER,
    @LayerID BIGINT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    IF @LayerDate IS NULL SET @LayerDate = CAST(GETDATE() AS DATE);

    DECLARE @MaxSequence INT;
    SELECT @MaxSequence = ISNULL(MAX(LayerSequence), -1) + 1
    FROM dbo.CostLayers
    WHERE CompanyID = @CompanyID
        AND ProductID = @ProductID
        AND WarehouseID = @WarehouseID
        AND IsDeleted = 0;

    INSERT INTO dbo.CostLayers (
        CompanyID, ProductID, WarehouseID, BatchID, LayerDate, LayerSequence,
        Quantity, UnitCost, RemainingQuantity, CurrencyCode,
        SourceReference, SourceID, SourceMovementID, CreatedBy
    ) VALUES (
        @CompanyID, @ProductID, @WarehouseID, @BatchID, @LayerDate, @MaxSequence,
        @Quantity, @UnitCost, @Quantity, @CurrencyCode,
        @SourceReference, @SourceID, @SourceMovementID, @CreatedBy
    );
    SET @LayerID = SCOPE_IDENTITY();
END;
GO
PRINT N'✅ [5.3] usp_Valuation_AddLayer created (Called from movements).';


IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Valuation_RevaluateProduct')
    DROP PROCEDURE dbo.usp_Valuation_RevaluateProduct;
GO
CREATE PROCEDURE dbo.usp_Valuation_RevaluateProduct
    @CompanyID UNIQUEIDENTIFIER,
    @ProductID INT,
    @WarehouseID BIGINT,
    @NewMethodCode NVARCHAR(20),
    @RevaluationDate DATE = NULL,
    @PerformedBy UNIQUEIDENTIFIER,
    @NewValue DECIMAL(18,6) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        BEGIN TRANSACTION;
        
        IF @RevaluationDate IS NULL SET @RevaluationDate = CAST(GETDATE() AS DATE);

        DECLARE @MethodID INT;
        SELECT @MethodID = ValuationMethodID 
        FROM dbo.ValuationMethods 
        WHERE CompanyID = @CompanyID AND MethodCode = @NewMethodCode AND IsActive = 1 AND IsDeleted = 0;
        
        IF @MethodID IS NULL THROW 50000, 'طريقة التقييم غير موجودة أو غير نشطة.', 1;

        DECLARE @CurrentQty DECIMAL(18,4), @CurrentValue DECIMAL(18,6), @AvgCost DECIMAL(18,6);
        
        SELECT @CurrentQty = AvailableQty, @AvgCost = AverageCost
        FROM dbo.StockBalances
        WHERE CompanyID = @CompanyID AND ProductID = @ProductID AND WarehouseID = @WarehouseID AND IsDeleted = 0;
        
        IF @CurrentQty IS NULL OR @CurrentQty = 0
        BEGIN
            SET @NewValue = 0;
            COMMIT TRANSACTION;
            RETURN;
        END

        SET @CurrentValue = @CurrentQty * ISNULL(@AvgCost, 0);

        INSERT INTO dbo.ProductValuation (
            CompanyID, ProductID, ValuationMethodID, EffectiveDate, IsActive, CreatedBy, Notes
        ) VALUES (
            @CompanyID, @ProductID, @MethodID, @RevaluationDate, 1, @PerformedBy, 
            N'إعادة تقييم من ' + @NewMethodCode
        );

        INSERT INTO dbo.ValuationHistory (
            CompanyID, ProductID, WarehouseID, ValuationDate, ValuationMethodID,
            TotalQuantity, TotalValue, AverageCost, CurrencyCode, Notes, CreatedBy
        ) VALUES (
            @CompanyID, @ProductID, @WarehouseID, @RevaluationDate, @MethodID,
            @CurrentQty, @CurrentValue, @AvgCost, 'SAR',
            N'إعادة تقييم بعد تغيير الطريقة إلى ' + @NewMethodCode,
            @PerformedBy
        );

        SET @NewValue = @CurrentValue;
        
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END;
GO
PRINT N'✅ [5.4] usp_Valuation_RevaluateProduct created (Method change).';


IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Valuation_InventoryReport')
    DROP PROCEDURE dbo.usp_Valuation_InventoryReport;
GO
CREATE PROCEDURE dbo.usp_Valuation_InventoryReport
    @CompanyID UNIQUEIDENTIFIER,
    @AsOfDate DATE = NULL,
    @WarehouseID BIGINT = NULL,
    @CategoryID INT = NULL,
    @BrandID INT = NULL,
    @ProductID INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOfDate IS NULL SET @AsOfDate = GETDATE();

    SELECT 
        p.ProductID,
        p.ProductCode,
        p.ProductNameAR,
        p.CategoryID,
        c.CategoryNameAR AS CategoryName,
        p.BrandID,
        b.BrandNameAR AS BrandName,
        sb.WarehouseID,
        w.WarehouseNameAR AS WarehouseName,
        sb.AvailableQty,
        sb.ReservedQty,
        sb.AvailableQty - sb.ReservedQty AS AvailableForSale,
        sb.AverageCost AS UnitCost,
        (sb.AvailableQty - sb.ReservedQty) * ISNULL(sb.AverageCost, 0) AS TotalValue,
        vm.MethodNameAR AS ValuationMethod,
        pv.EffectiveDate AS ValuationEffectiveDate,
        CASE 
            WHEN (sb.AvailableQty - sb.ReservedQty) = 0 THEN N'نفد المخزون'
            WHEN sb.AvailableQty <= p.ReorderLevel THEN N'يحتاج إعادة طلب'
            ELSE N'متوفر'
        END AS StockStatus,
        DATEDIFF(DAY, ISNULL(sb.LastUpdated, GETDATE()), GETDATE()) AS DaysSinceLastUpdate
    FROM dbo.StockBalances sb
    INNER JOIN dbo.Products p ON sb.ProductID = p.ProductID
    INNER JOIN dbo.Warehouses w ON sb.WarehouseID = w.WarehouseID
    LEFT JOIN dbo.ProductCategories c ON p.CategoryID = c.CategoryID
    LEFT JOIN dbo.Brands b ON p.BrandID = b.BrandID
    LEFT JOIN dbo.ProductValuation pv ON p.ProductID = pv.ProductID AND pv.EffectiveDate <= @AsOfDate AND pv.IsActive = 1 AND pv.IsDeleted = 0
    LEFT JOIN dbo.ValuationMethods vm ON pv.ValuationMethodID = vm.ValuationMethodID
    WHERE sb.CompanyID = @CompanyID
        AND sb.IsDeleted = 0
        AND (@WarehouseID IS NULL OR sb.WarehouseID = @WarehouseID)
        AND (@CategoryID IS NULL OR p.CategoryID = @CategoryID)
        AND (@BrandID IS NULL OR p.BrandID = @BrandID)
        AND (@ProductID IS NULL OR p.ProductID = @ProductID)
    ORDER BY p.ProductNameAR;
END;
GO
PRINT N'✅ [5.5] usp_Valuation_InventoryReport created (Advanced).';


IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Valuation_HistoryReport')
    DROP PROCEDURE dbo.usp_Valuation_HistoryReport;
GO
CREATE PROCEDURE dbo.usp_Valuation_HistoryReport
    @CompanyID UNIQUEIDENTIFIER,
    @ProductID INT,
    @WarehouseID BIGINT = NULL,
    @FromDate DATE = NULL,
    @ToDate DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @FromDate IS NULL SET @FromDate = DATEADD(MONTH, -12, GETDATE());
    IF @ToDate IS NULL SET @ToDate = GETDATE();

    SELECT 
        vh.ValuationHistoryID,
        vh.ValuationDate,
        p.ProductCode,
        p.ProductNameAR,
        w.WarehouseNameAR AS WarehouseName,
        vh.TotalQuantity,
        vh.TotalValue,
        vh.AverageCost,
        vm.MethodNameAR AS ValuationMethod,
        vh.CreatedAt AS RecordedAt
    FROM dbo.ValuationHistory vh
    INNER JOIN dbo.Products p ON vh.ProductID = p.ProductID
    INNER JOIN dbo.Warehouses w ON vh.WarehouseID = w.WarehouseID
    LEFT JOIN dbo.ValuationMethods vm ON vh.ValuationMethodID = vm.ValuationMethodID
    WHERE vh.CompanyID = @CompanyID
        AND vh.ProductID = @ProductID
        AND vh.IsDeleted = 0
        AND (@WarehouseID IS NULL OR vh.WarehouseID = @WarehouseID)
        AND vh.ValuationDate BETWEEN @FromDate AND @ToDate
    ORDER BY vh.ValuationDate DESC;
END;
GO
PRINT N'✅ [5.6] usp_Valuation_HistoryReport created (Historical trends).';


IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Valuation_GetValueAtDate')
    DROP PROCEDURE dbo.usp_Valuation_GetValueAtDate;
GO
CREATE PROCEDURE dbo.usp_Valuation_GetValueAtDate
    @CompanyID UNIQUEIDENTIFIER,
    @AsOfDate DATE,
    @WarehouseID BIGINT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT 
        p.ProductID,
        p.ProductCode,
        p.ProductNameAR,
        SUM(cl.RemainingQuantity * cl.UnitCost) AS TotalValue,
        SUM(cl.RemainingQuantity) AS TotalQuantity,
        CASE 
            WHEN SUM(cl.RemainingQuantity) > 0 
            THEN SUM(cl.RemainingQuantity * cl.UnitCost) / SUM(cl.RemainingQuantity) 
            ELSE 0 
        END AS AverageCost
    FROM dbo.CostLayers cl
    INNER JOIN dbo.Products p ON cl.ProductID = p.ProductID
    WHERE cl.CompanyID = @CompanyID
        AND (@WarehouseID IS NULL OR cl.WarehouseID = @WarehouseID)
        AND cl.IsConsumed = 0
        AND cl.IsDeleted = 0
        AND cl.LayerDate <= @AsOfDate
    GROUP BY p.ProductID, p.ProductCode, p.ProductNameAR
    ORDER BY p.ProductNameAR;
END;
GO
PRINT N'✅ [5.7] usp_Valuation_GetValueAtDate created (Financial reporting).';

-- ========================================================================
-- ========================================================================

IF EXISTS (SELECT 1 FROM sys.triggers WHERE name = 'trg_StockMovements_AddCostLayer')
    DROP TRIGGER dbo.trg_StockMovements_AddCostLayer;
GO
CREATE TRIGGER dbo.trg_StockMovements_AddCostLayer
ON dbo.StockMovements
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @MovementID BIGINT, @CompanyID UNIQUEIDENTIFIER, @ProductID INT;
    DECLARE @WarehouseID BIGINT, @Quantity DECIMAL(18,4), @UnitCost DECIMAL(18,6);
    DECLARE @MovementType NVARCHAR(30), @CreatedBy UNIQUEIDENTIFIER;

    DECLARE cur CURSOR FOR
        SELECT MovementID, CompanyID, ProductID, WarehouseIDTo, Quantity, UnitCost, MovementType, CreatedBy
        FROM inserted
        WHERE MovementType IN ('PURCHASE', 'RETURN_SALE', 'TRANSFER_IN', 'COUNT_OPEN')
          AND UnitCost IS NOT NULL AND UnitCost > 0;

    OPEN cur;
    FETCH NEXT FROM cur INTO @MovementID, @CompanyID, @ProductID, @WarehouseID, @Quantity, @UnitCost, @MovementType, @CreatedBy;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF @WarehouseID IS NOT NULL
        BEGIN
            EXEC dbo.usp_Valuation_AddLayer
                @CompanyID = @CompanyID,
                @ProductID = @ProductID,
                @WarehouseID = @WarehouseID,
                @Quantity = @Quantity,
                @UnitCost = @UnitCost,
                @SourceMovementID = @MovementID,
                @SourceReference = @MovementType,
                @SourceID = @MovementID,
                @CreatedBy = @CreatedBy,
                @LayerID = NULL;
        END
        FETCH NEXT FROM cur INTO @MovementID, @CompanyID, @ProductID, @WarehouseID, @Quantity, @UnitCost, @MovementType, @CreatedBy;
    END
    CLOSE cur; DEALLOCATE cur;
END;
GO
PRINT N'✅ [6] trg_StockMovements_AddCostLayer created (Auto-layer creation).';

-- ========================================================================
-- ========================================================================

IF EXISTS (SELECT 1 FROM sys.views WHERE name = 'vw_ActiveCostLayers')
    DROP VIEW dbo.vw_ActiveCostLayers;
GO
CREATE VIEW dbo.vw_ActiveCostLayers
AS
SELECT 
    cl.LayerID,
    cl.ProductID,
    p.ProductCode,
    p.ProductNameAR,
    cl.WarehouseID,
    w.WarehouseNameAR,
    cl.LayerDate,
    cl.LayerSequence,
    cl.Quantity,
    cl.UnitCost,
    cl.RemainingQuantity,
    cl.RemainingQuantity * cl.UnitCost AS RemainingValue,
    cl.CurrencyCode,
    cl.SourceReference,
    CASE WHEN cl.LayerSequence = 0 THEN N'FIFO' ELSE N'LIFO' END AS LayerType
FROM dbo.CostLayers cl
INNER JOIN dbo.Products p ON cl.ProductID = p.ProductID
INNER JOIN dbo.Warehouses w ON cl.WarehouseID = w.WarehouseID
WHERE cl.IsConsumed = 0 AND cl.IsDeleted = 0;
GO
PRINT N'✅ [7.1] vw_ActiveCostLayers created.';

IF EXISTS (SELECT 1 FROM sys.views WHERE name = 'vw_InventoryValuation')
    DROP VIEW dbo.vw_InventoryValuation;
GO
CREATE VIEW dbo.vw_InventoryValuation
AS
SELECT 
    p.ProductID,
    p.ProductCode,
    p.ProductNameAR,
    c.CategoryNameAR AS CategoryName,
    b.BrandNameAR AS BrandName,
    w.WarehouseNameAR AS WarehouseName,
    sb.AvailableQty - sb.ReservedQty AS AvailableQty,
    sb.AverageCost,
    (sb.AvailableQty - sb.ReservedQty) * ISNULL(sb.AverageCost, 0) AS TotalValue,
    vm.MethodNameAR AS ValuationMethod
FROM dbo.StockBalances sb
INNER JOIN dbo.Products p ON sb.ProductID = p.ProductID
INNER JOIN dbo.Warehouses w ON sb.WarehouseID = w.WarehouseID
LEFT JOIN dbo.ProductCategories c ON p.CategoryID = c.CategoryID
LEFT JOIN dbo.Brands b ON p.BrandID = b.BrandID
LEFT JOIN dbo.ProductValuation pv ON p.ProductID = pv.ProductID AND pv.IsActive = 1 AND pv.IsDeleted = 0
LEFT JOIN dbo.ValuationMethods vm ON pv.ValuationMethodID = vm.ValuationMethodID
WHERE sb.IsDeleted = 0 AND (sb.AvailableQty - sb.ReservedQty) > 0;
GO
PRINT N'✅ [7.2] vw_InventoryValuation created.';

-- ========================================================================
-- ========================================================================
    DECLARE @SystemCompanyID UNIQUEIDENTIFIER;
    DECLARE @SystemUserID UNIQUEIDENTIFIER = '00000000-0000-0000-0000-000000000001';
    SELECT TOP 1 @SystemCompanyID = CompanyID FROM MST.dbo.Companies;

    IF NOT EXISTS (SELECT 1 FROM dbo.ValuationMethods WHERE MethodCode = 'AVG' AND CompanyID = @SystemCompanyID)
    BEGIN
        INSERT INTO dbo.ValuationMethods (CompanyID, MethodCode, MethodNameAR, MethodNameEN, MethodDescription, IsDefault, IsActive, CreatedBy)
        VALUES 
            (@SystemCompanyID, 'FIFO', N'الوارد أولاً صادر أولاً', N'FIFO (First In First Out)', 
             N'يتم احتساب تكلفة المبيعات بناءً على أقدم تكلفة شراء.', 0, 1, @SystemUserID),
            (@SystemCompanyID, 'LIFO', N'الوارد أخيراً صادر أولاً', N'LIFO (Last In First Out)',
             N'يتم احتساب تكلفة المبيعات بناءً على أحدث تكلفة شراء.', 0, 1, @SystemUserID),
            (@SystemCompanyID, 'AVG', N'المتوسط المرجح', N'Weighted Average',
             N'يتم احتساب تكلفة المبيعات بناءً على متوسط جميع تكاليف الشراء.', 1, 1, @SystemUserID);
        PRINT N'✅ [8] Valuation Methods seeded (FIFO, LIFO, AVG).';
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