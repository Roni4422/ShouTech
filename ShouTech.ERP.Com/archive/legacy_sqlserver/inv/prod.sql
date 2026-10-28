-- ========================================================================
-- FILE: dba/inv/prod_ultimate.sql
-- PROJECT: SHOUTECH ERP V10 - ULTIMATE PRODUCTS MASTER (10.10.0)
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
PRINT N'SHOUTECH ERP V10 – ULTIMATE PRODUCTS MASTER (10.10.0)';
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
        PRINT N'✅ [1] ProductCategories created.';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ProductCategories_Company_Active 
        ON dbo.ProductCategories(CompanyID, IsActive) WHERE IsDeleted = 0;
    GO

-- ========================================================================
-- ========================================================================
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
            Description NVARCHAR(500) NULL,
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
        PRINT N'✅ [2] Brands created.';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Brands_Company_Active 
        ON dbo.Brands(CompanyID, IsActive) WHERE IsDeleted = 0;
    GO

-- ========================================================================
-- ========================================================================
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
        PRINT N'✅ [3] UnitsOfMeasure created.';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_UnitsOfMeasure_Company_Active 
        ON dbo.UnitsOfMeasure(CompanyID, IsActive) WHERE IsDeleted = 0;
    GO

-- ========================================================================
-- ========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Products')
    BEGIN
        CREATE TABLE dbo.Products (
            ProductID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            
            ProductCode NVARCHAR(50) NOT NULL,
            ProductNameAR NVARCHAR(255) NOT NULL,
            ProductNameEN NVARCHAR(255) NULL,
            ProductDescription NVARCHAR(MAX) NULL,
            ShortDescription NVARCHAR(500) NULL,
            
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
            MinSalePrice DECIMAL(18,6) NULL,
            MaxSalePrice DECIMAL(18,6) NULL,
            
            TaxCategory NVARCHAR(20) NOT NULL DEFAULT 'STANDARD',
            TaxRate DECIMAL(5,2) NOT NULL DEFAULT 15,
            TaxExemptReason NVARCHAR(200) NULL,
            ZATCAItemCode NVARCHAR(50) NULL,          -- رقم السلع الضريبي (ZATCA)
            ZATCAUnitCode NVARCHAR(20) NULL,           -- رمز الوحدة الضريبي
            
            IsStockItem BIT NOT NULL DEFAULT 1,
            IsServiceItem BIT NOT NULL DEFAULT 0,
            IsPOSItem BIT NOT NULL DEFAULT 1,
            IsSerialized BIT NOT NULL DEFAULT 0,
            IsBatchTracked BIT NOT NULL DEFAULT 0,
            IsExpiryTracked BIT NOT NULL DEFAULT 0,
            ExpiryDays INT NULL,
            ShelfLifeDays INT NULL,
            IsCompositeProduct BIT NOT NULL DEFAULT 0,   -- منتج مركب (BOM)
            
            ReorderLevel DECIMAL(18,3) NOT NULL DEFAULT 0,
            ReorderQuantity DECIMAL(18,3) NOT NULL DEFAULT 0,
            SafetyStock DECIMAL(18,3) NOT NULL DEFAULT 0,
            MaxStock DECIMAL(18,3) NULL,
            
            Weight DECIMAL(18,6) NULL,
            Length DECIMAL(18,6) NULL,
            Width DECIMAL(18,6) NULL,
            Height DECIMAL(18,6) NULL,
            Volume DECIMAL(18,6) NULL,                 -- الحجم (محسوب)
            
            MainImagePath NVARCHAR(500) NULL,
            
            DemandVelocity TINYINT NULL,               -- 1-5
            ProfitMargin DECIMAL(5,2) NULL,
            ProductScore DECIMAL(5,2) NULL,
            IsRecommended BIT NOT NULL DEFAULT 0,
            RecommendedScore DECIMAL(5,2) NULL,
            
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
        PRINT N'✅ [4] Products table created (Ultimate 10/10 with ZATCA support).';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Products_POS_Search 
        ON dbo.Products(CompanyID, IsPOSItem, IsActive) 
        INCLUDE (ProductCode, ProductNameAR, Barcode, QuickCode, DefaultSalePrice, MainImagePath, TaxRate, ZATCAItemCode) 
        WHERE IsDeleted = 0;

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Products_Barcode_Search 
        ON dbo.Products(Barcode) WHERE Barcode IS NOT NULL AND IsDeleted = 0;

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Products_Name_Search 
        ON dbo.Products(ProductNameAR) WHERE IsDeleted = 0 AND IsActive = 1;

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Products_Reorder 
        ON dbo.Products(ReorderLevel, IsActive) WHERE IsDeleted = 0;

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Products_Category 
        ON dbo.Products(CategoryID, IsActive) WHERE IsDeleted = 0;

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Products_ZATCA 
        ON dbo.Products(ZATCAItemCode) WHERE ZATCAItemCode IS NOT NULL AND IsDeleted = 0;

    CREATE NONCLUSTERED COLUMNSTORE INDEX IF NOT EXISTS CSIX_Products_Analytics 
        ON dbo.Products (CategoryID, BrandID, IsActive, CreatedAt, DefaultSalePrice, CostPrice, TaxCategory, IsCompositeProduct);
    GO

-- ========================================================================
-- ========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ProductImages')
    BEGIN
        CREATE TABLE dbo.ProductImages (
            ImageID INT IDENTITY(1,1) NOT NULL,
            ProductID INT NOT NULL,
            ImagePath NVARCHAR(500) NOT NULL,
            ImageOrder TINYINT NOT NULL DEFAULT 0,
            IsPrimary BIT NOT NULL DEFAULT 0,
            Title NVARCHAR(200) NULL,
            AltText NVARCHAR(200) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_ProductImages PRIMARY KEY CLUSTERED (ImageID),
            CONSTRAINT FK_ProductImages_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID) ON DELETE CASCADE
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ [5] ProductImages created (Multiple images).';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ProductImages_Product 
        ON dbo.ProductImages(ProductID, ImageOrder) WHERE IsDeleted = 0;
    GO

-- ========================================================================
-- ========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ProductAttributes')
    BEGIN
        CREATE TABLE dbo.ProductAttributes (
            AttributeID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ProductID INT NOT NULL,
            AttributeName NVARCHAR(100) NOT NULL,
            AttributeValue NVARCHAR(500) NOT NULL,
            AttributeGroup NVARCHAR(50) NULL,
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
            CONSTRAINT FK_ProductAttributes_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID) ON DELETE CASCADE,
            CONSTRAINT FK_ProductAttributes_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ [6] ProductAttributes created (Custom attributes).';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ProductAttributes_Product 
        ON dbo.ProductAttributes(ProductID, AttributeName) WHERE IsDeleted = 0;
    GO

-- ========================================================================
-- ========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ProductBOM')
    BEGIN
        CREATE TABLE dbo.ProductBOM (
            BOMID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ProductID INT NOT NULL,              -- المنتج النهائي (الأب)
            ComponentProductID INT NOT NULL,     -- المادة الخام (الابن)
            Quantity DECIMAL(18,6) NOT NULL,
            UOMID INT NOT NULL,
            WastePercentage DECIMAL(5,2) NOT NULL DEFAULT 0,
            CostPerUnit DECIMAL(18,6) NULL,
            TotalCost DECIMAL(18,6) NULL,
            StartDate DATE NULL,
            EndDate DATE NULL,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_ProductBOM PRIMARY KEY CLUSTERED (BOMID),
            CONSTRAINT FK_ProductBOM_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID) ON DELETE CASCADE,
            CONSTRAINT FK_ProductBOM_Component FOREIGN KEY (ComponentProductID) REFERENCES dbo.Products(ProductID),
            CONSTRAINT FK_ProductBOM_UOM FOREIGN KEY (UOMID) REFERENCES dbo.UnitsOfMeasure(UOMID),
            CONSTRAINT FK_ProductBOM_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ [7] ProductBOM created (Bill of Materials).';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ProductBOM_Product 
        ON dbo.ProductBOM(ProductID, IsActive) WHERE IsDeleted = 0;

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ProductBOM_Component 
        ON dbo.ProductBOM(ComponentProductID, IsActive) WHERE IsDeleted = 0;
    GO

-- ========================================================================
-- ========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PriceLists')
    BEGIN
        CREATE TABLE dbo.PriceLists (
            PriceListID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            PriceListCode NVARCHAR(50) NOT NULL,
            PriceListNameAR NVARCHAR(200) NOT NULL,
            PriceListNameEN NVARCHAR(200) NOT NULL,
            CurrencyCode NVARCHAR(3) DEFAULT 'SAR',
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
            CONSTRAINT PK_PriceLists PRIMARY KEY CLUSTERED (PriceListID),
            CONSTRAINT UQ_PriceLists_Code UNIQUE (CompanyID, PriceListCode),
            CONSTRAINT FK_PriceLists_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ [8] PriceLists created.';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_PriceLists_Company_Active 
        ON dbo.PriceLists(CompanyID, IsActive) WHERE IsDeleted = 0;
    GO

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PriceListItems')
    BEGIN
        CREATE TABLE dbo.PriceListItems (
            PriceListItemID BIGINT IDENTITY(1,1) NOT NULL,
            PriceListID INT NOT NULL,
            ProductID INT NOT NULL,
            Price DECIMAL(18,6) NOT NULL,
            MinQuantity DECIMAL(18,3) NOT NULL DEFAULT 1,
            StartDate DATE NULL,
            EndDate DATE NULL,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_PriceListItems PRIMARY KEY CLUSTERED (PriceListItemID),
            CONSTRAINT FK_PriceListItems_PriceList FOREIGN KEY (PriceListID) REFERENCES dbo.PriceLists(PriceListID) ON DELETE CASCADE,
            CONSTRAINT FK_PriceListItems_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ [8.1] PriceListItems created.';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_PriceListItems_Product 
        ON dbo.PriceListItems(ProductID, PriceListID) INCLUDE (Price, MinQuantity) WHERE IsDeleted = 0;
    GO

-- ========================================================================
-- ========================================================================

IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Products_SearchAdvanced')
    DROP PROCEDURE dbo.usp_Products_SearchAdvanced;
GO
CREATE PROCEDURE dbo.usp_Products_SearchAdvanced
    @CompanyID UNIQUEIDENTIFIER,
    @SearchTerm NVARCHAR(200) = NULL,
    @CategoryID INT = NULL,
    @BrandID INT = NULL,
    @IsActive BIT = 1,
    @IsPOSOnly BIT = 0,
    @HasStock BIT = 0,
    @ExpiringInDays INT = NULL,
    @MinPrice DECIMAL(18,6) = NULL,
    @MaxPrice DECIMAL(18,6) = NULL,
    @TopN INT = 100
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
        p.CostPrice,
        p.TaxRate,
        p.TaxCategory,
        p.ZATCAItemCode,
        p.IsSerialized,
        p.IsBatchTracked,
        p.IsExpiryTracked,
        p.MainImagePath,
        c.CategoryNameAR AS CategoryName,
        b.BrandNameAR AS BrandName,
        ISNULL(s.AvailableQty, 0) AS AvailableQty,
        ISNULL(s.ReservedQty, 0) AS ReservedQty,
        (ISNULL(s.AvailableQty, 0) - ISNULL(s.ReservedQty, 0)) AS AvailableForSale,
        p.IsActive,
        p.IsBlocked,
        p.CreatedAt
    FROM dbo.Products p
    LEFT JOIN dbo.ProductCategories c ON p.CategoryID = c.CategoryID
    LEFT JOIN dbo.Brands b ON p.BrandID = b.BrandID
    LEFT JOIN dbo.StockBalances s ON p.ProductID = s.ProductID 
        AND s.WarehouseID = (SELECT TOP 1 WarehouseID FROM dbo.Warehouses WHERE CompanyID = @CompanyID AND IsDefault = 1 AND IsDeleted = 0)
        AND s.IsDeleted = 0
    WHERE p.CompanyID = @CompanyID
        AND p.IsDeleted = 0
        AND (@IsActive IS NULL OR p.IsActive = @IsActive)
        AND (@IsPOSOnly = 0 OR p.IsPOSItem = 1)
        AND (@CategoryID IS NULL OR p.CategoryID = @CategoryID)
        AND (@BrandID IS NULL OR p.BrandID = @BrandID)
        AND (@SearchTerm IS NULL 
            OR p.ProductCode LIKE N'%' + @SearchTerm + N'%'
            OR p.ProductNameAR LIKE N'%' + @SearchTerm + N'%'
            OR p.ProductNameEN LIKE N'%' + @SearchTerm + N'%'
            OR p.Barcode LIKE N'%' + @SearchTerm + N'%'
            OR p.QuickCode LIKE N'%' + @SearchTerm + N'%'
            OR p.SKU LIKE N'%' + @SearchTerm + N'%'
            OR p.ZATCAItemCode LIKE N'%' + @SearchTerm + N'%')
        AND (@MinPrice IS NULL OR p.DefaultSalePrice >= @MinPrice)
        AND (@MaxPrice IS NULL OR p.DefaultSalePrice <= @MaxPrice)
        AND (@HasStock = 0 OR ISNULL(s.AvailableQty, 0) > 0)
    ORDER BY 
        CASE WHEN p.IsRecommended = 1 THEN 0 ELSE 1 END,
        p.ProductNameAR;
END;
GO
PRINT N'✅ [9.1] usp_Products_SearchAdvanced created.';

IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Products_CopyProduct')
    DROP PROCEDURE dbo.usp_Products_CopyProduct;
GO
CREATE PROCEDURE dbo.usp_Products_CopyProduct
    @SourceProductID INT,
    @NewProductCode NVARCHAR(50),
    @NewProductNameAR NVARCHAR(255) = NULL,
    @CopyAttributes BIT = 1,
    @CopyImages BIT = 1,
    @CopyBOM BIT = 1,
    @CreatedBy UNIQUEIDENTIFIER,
    @NewProductID INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        BEGIN TRANSACTION;

        INSERT INTO dbo.Products (
            CompanyID, ProductCode, ProductNameAR, ProductNameEN, ProductDescription, ShortDescription,
            CategoryID, BrandID, PrimaryUOMID, SaleUOMID, PurchaseUOMID,
            Barcode, QuickCode, SKU,
            DefaultSalePrice, DefaultPurchasePrice, CostPrice, 
            TaxCategory, TaxRate, TaxExemptReason, ZATCAItemCode, ZATCAUnitCode,
            IsStockItem, IsServiceItem, IsPOSItem, IsSerialized, IsBatchTracked, IsExpiryTracked,
            ExpiryDays, ShelfLifeDays, IsCompositeProduct,
            ReorderLevel, ReorderQuantity, SafetyStock, MaxStock,
            Weight, Length, Width, Height, Volume,
            MainImagePath,
            DemandVelocity, ProfitMargin, ProductScore, IsRecommended, RecommendedScore,
            IsActive, IsBlocked, BlockReason, DiscontinuedDate,
            IsDeleted, CreatedBy, CreatedAt
        )
        SELECT 
            CompanyID, @NewProductCode, ISNULL(@NewProductNameAR, ProductNameAR + N' (نسخة)'), ProductNameEN, ProductDescription, ShortDescription,
            CategoryID, BrandID, PrimaryUOMID, SaleUOMID, PurchaseUOMID,
            NULL, QuickCode, NULL,  -- لا ننسخ Barcode و SKU
            DefaultSalePrice, DefaultPurchasePrice, CostPrice,
            TaxCategory, TaxRate, TaxExemptReason, ZATCAItemCode, ZATCAUnitCode,
            IsStockItem, IsServiceItem, IsPOSItem, IsSerialized, IsBatchTracked, IsExpiryTracked,
            ExpiryDays, ShelfLifeDays, IsCompositeProduct,
            ReorderLevel, ReorderQuantity, SafetyStock, MaxStock,
            Weight, Length, Width, Height, Volume,
            MainImagePath,
            DemandVelocity, ProfitMargin, ProductScore, IsRecommended, RecommendedScore,
            IsActive, IsBlocked, BlockReason, DiscontinuedDate,
            0, @CreatedBy, SYSUTCDATETIME()
        FROM dbo.Products
        WHERE ProductID = @SourceProductID AND IsDeleted = 0;

        SET @NewProductID = SCOPE_IDENTITY();

        IF @CopyAttributes = 1
        BEGIN
            INSERT INTO dbo.ProductAttributes (CompanyID, ProductID, AttributeName, AttributeValue, AttributeGroup, IsActive, CreatedBy)
            SELECT CompanyID, @NewProductID, AttributeName, AttributeValue, AttributeGroup, IsActive, @CreatedBy
            FROM dbo.ProductAttributes
            WHERE ProductID = @SourceProductID AND IsDeleted = 0;
        END

        IF @CopyImages = 1
        BEGIN
            INSERT INTO dbo.ProductImages (ProductID, ImagePath, ImageOrder, IsPrimary, Title, AltText, CreatedBy)
            SELECT @NewProductID, ImagePath, ImageOrder, IsPrimary, Title, AltText, @CreatedBy
            FROM dbo.ProductImages
            WHERE ProductID = @SourceProductID AND IsDeleted = 0;
        END

        IF @CopyBOM = 1
        BEGIN
            INSERT INTO dbo.ProductBOM (
                CompanyID, ProductID, ComponentProductID, Quantity, UOMID, 
                WastePercentage, CostPerUnit, TotalCost, StartDate, EndDate, IsActive, CreatedBy
            )
            SELECT 
                CompanyID, @NewProductID, ComponentProductID, Quantity, UOMID,
                WastePercentage, CostPerUnit, TotalCost, StartDate, EndDate, IsActive, @CreatedBy
            FROM dbo.ProductBOM
            WHERE ProductID = @SourceProductID AND IsDeleted = 0;
        END

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END;
GO
PRINT N'✅ [9.2] usp_Products_CopyProduct created.';

IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Products_UpdatePrices')
    DROP PROCEDURE dbo.usp_Products_UpdatePrices;
GO
CREATE PROCEDURE dbo.usp_Products_UpdatePrices
    @CompanyID UNIQUEIDENTIFIER,
    @Percentage DECIMAL(5,2),
    @PriceType NVARCHAR(20) = 'SALE',  -- SALE, PURCHASE, COST
    @CategoryID INT = NULL,
    @BrandID INT = NULL,
    @UpdatedBy UNIQUEIDENTIFIER,
    @AffectedRows INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        BEGIN TRANSACTION;

        IF @PriceType = 'SALE'
        BEGIN
            UPDATE dbo.Products
            SET DefaultSalePrice = DefaultSalePrice * (1 + @Percentage / 100),
                UpdatedBy = @UpdatedBy,
                UpdatedAt = SYSUTCDATETIME()
            WHERE CompanyID = @CompanyID
                AND IsDeleted = 0
                AND (@CategoryID IS NULL OR CategoryID = @CategoryID)
                AND (@BrandID IS NULL OR BrandID = @BrandID);
        END
        ELSE IF @PriceType = 'PURCHASE'
        BEGIN
            UPDATE dbo.Products
            SET DefaultPurchasePrice = DefaultPurchasePrice * (1 + @Percentage / 100),
                UpdatedBy = @UpdatedBy,
                UpdatedAt = SYSUTCDATETIME()
            WHERE CompanyID = @CompanyID
                AND IsDeleted = 0
                AND (@CategoryID IS NULL OR CategoryID = @CategoryID)
                AND (@BrandID IS NULL OR BrandID = @BrandID);
        END
        ELSE IF @PriceType = 'COST'
        BEGIN
            UPDATE dbo.Products
            SET CostPrice = CostPrice * (1 + @Percentage / 100),
                UpdatedBy = @UpdatedBy,
                UpdatedAt = SYSUTCDATETIME()
            WHERE CompanyID = @CompanyID
                AND IsDeleted = 0
                AND (@CategoryID IS NULL OR CategoryID = @CategoryID)
                AND (@BrandID IS NULL OR BrandID = @BrandID);
        END

        SET @AffectedRows = @@ROWCOUNT;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END;
GO
PRINT N'✅ [9.3] usp_Products_UpdatePrices created.';

IF EXISTS (SELECT 1 FROM sys.procedures WHERE name = 'usp_Products_GetDetailsWithPrice')
    DROP PROCEDURE dbo.usp_Products_GetDetailsWithPrice;
GO
CREATE PROCEDURE dbo.usp_Products_GetDetailsWithPrice
    @ProductID INT,
    @CustomerID BIGINT = NULL,
    @Quantity DECIMAL(18,3) = 1,
    @WarehouseID BIGINT = NULL,
    @CurrencyCode NVARCHAR(3) = 'SAR'
AS
BEGIN
    SET NOCOUNT ON;
    SELECT 
        p.ProductID,
        p.ProductCode,
        p.ProductNameAR,
        p.Barcode,
        p.DefaultSalePrice AS BasePrice,
        p.TaxRate,
        p.TaxCategory,
        p.ZATCAItemCode,
        p.ZATCAUnitCode,
        p.IsSerialized,
        p.IsBatchTracked,
        p.IsExpiryTracked,
        p.ExpiryDays,
        ISNULL(s.AvailableQty, 0) AS AvailableQty,
        ISNULL(s.ReservedQty, 0) AS ReservedQty,
        (ISNULL(s.AvailableQty, 0) - ISNULL(s.ReservedQty, 0)) AS AvailableForSale,
        p.DefaultSalePrice AS EffectivePrice
    FROM dbo.Products p
    LEFT JOIN dbo.StockBalances s ON p.ProductID = s.ProductID AND s.WarehouseID = @WarehouseID AND s.IsDeleted = 0
    WHERE p.ProductID = @ProductID AND p.IsDeleted = 0 AND p.IsActive = 1;
END;
GO
PRINT N'✅ [9.4] usp_Products_GetDetailsWithPrice created.';

-- ========================================================================
-- ========================================================================

IF EXISTS (SELECT 1 FROM sys.views WHERE name = 'vw_ProductsWithStock')
    DROP VIEW dbo.vw_ProductsWithStock;
GO
CREATE VIEW dbo.vw_ProductsWithStock
AS
SELECT 
    p.ProductID,
    p.ProductCode,
    p.ProductNameAR,
    p.ProductNameEN,
    p.Barcode,
    p.QuickCode,
    p.DefaultSalePrice,
    p.CostPrice,
    p.TaxRate,
    p.TaxCategory,
    p.ZATCAItemCode,
    p.IsSerialized,
    p.IsBatchTracked,
    p.IsExpiryTracked,
    p.IsCompositeProduct,
    p.IsActive,
    p.IsBlocked,
    p.MainImagePath,
    c.CategoryNameAR AS CategoryName,
    b.BrandNameAR AS BrandName,
    u.UOMNameAR AS PrimaryUOM,
    ISNULL(s.AvailableQty, 0) AS AvailableQty,
    ISNULL(s.ReservedQty, 0) AS ReservedQty,
    (ISNULL(s.AvailableQty, 0) - ISNULL(s.ReservedQty, 0)) AS AvailableForSale,
    p.ReorderLevel,
    p.SafetyStock,
    p.ProductScore,
    p.DemandVelocity,
    p.IsRecommended
FROM dbo.Products p
LEFT JOIN dbo.ProductCategories c ON p.CategoryID = c.CategoryID
LEFT JOIN dbo.Brands b ON p.BrandID = b.BrandID
LEFT JOIN dbo.UnitsOfMeasure u ON p.PrimaryUOMID = u.UOMID
LEFT JOIN dbo.StockBalances s ON p.ProductID = s.ProductID 
    AND s.WarehouseID = (SELECT TOP 1 WarehouseID FROM dbo.Warehouses WHERE CompanyID = p.CompanyID AND IsDefault = 1 AND IsDeleted = 0)
    AND s.IsDeleted = 0
WHERE p.IsDeleted = 0;
GO
PRINT N'✅ [10.1] vw_ProductsWithStock created.';

IF EXISTS (SELECT 1 FROM sys.views WHERE name = 'vw_ProductBOM')
    DROP VIEW dbo.vw_ProductBOM;
GO
CREATE VIEW dbo.vw_ProductBOM
AS
SELECT 
    bom.BOMID,
    bom.ProductID AS ParentProductID,
    pParent.ProductNameAR AS ParentProductName,
    bom.ComponentProductID,
    pComp.ProductNameAR AS ComponentProductName,
    bom.Quantity,
    u.UOMNameAR AS UOM,
    bom.WastePercentage,
    bom.CostPerUnit,
    bom.TotalCost,
    bom.IsActive,
    bom.StartDate,
    bom.EndDate
FROM dbo.ProductBOM bom
INNER JOIN dbo.Products pParent ON bom.ProductID = pParent.ProductID
INNER JOIN dbo.Products pComp ON bom.ComponentProductID = pComp.ProductID
LEFT JOIN dbo.UnitsOfMeasure u ON bom.UOMID = u.UOMID
WHERE bom.IsDeleted = 0;
GO
PRINT N'✅ [10.2] vw_ProductBOM created.';

IF EXISTS (SELECT 1 FROM sys.views WHERE name = 'vw_ProductsDashboard')
    DROP VIEW dbo.vw_ProductsDashboard;
GO
CREATE VIEW dbo.vw_ProductsDashboard
AS
SELECT 
    p.CompanyID,
    COUNT(p.ProductID) AS TotalProducts,
    SUM(CASE WHEN p.IsActive = 1 THEN 1 ELSE 0 END) AS ActiveProducts,
    SUM(CASE WHEN p.IsBlocked = 1 THEN 1 ELSE 0 END) AS BlockedProducts,
    SUM(CASE WHEN p.IsPOSItem = 1 THEN 1 ELSE 0 END) AS POSItems,
    SUM(CASE WHEN p.IsCompositeProduct = 1 THEN 1 ELSE 0 END) AS CompositeProducts,
    SUM(CASE WHEN p.IsSerialized = 1 THEN 1 ELSE 0 END) AS SerializedProducts,
    SUM(CASE WHEN p.IsBatchTracked = 1 THEN 1 ELSE 0 END) AS BatchProducts,
    SUM(CASE WHEN p.IsExpiryTracked = 1 THEN 1 ELSE 0 END) AS ExpiryProducts,
    SUM(CASE WHEN p.IsRecommended = 1 THEN 1 ELSE 0 END) AS RecommendedProducts
FROM dbo.Products p
WHERE p.IsDeleted = 0
GROUP BY p.CompanyID;
GO
PRINT N'✅ [10.3] vw_ProductsDashboard created.';

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
        PRINT N'✅ Default Units of Measure seeded.';
    END

    IF NOT EXISTS (SELECT 1 FROM dbo.ProductCategories WHERE CategoryCode = 'GENERAL' AND CompanyID = @SystemCompanyID)
    BEGIN
        INSERT INTO dbo.ProductCategories (CompanyID, CategoryCode, CategoryNameAR, CategoryNameEN, IsActive, CreatedBy)
        VALUES 
            (@SystemCompanyID, 'GENERAL', N'عام', N'General', 1, @SystemUserID),
            (@SystemCompanyID, 'FOOD', N'مواد غذائية', N'Food', 1, @SystemUserID),
            (@SystemCompanyID, 'BEVERAGE', N'مشروبات', N'Beverages', 1, @SystemUserID),
            (@SystemCompanyID, 'ELECTRONICS', N'إلكترونيات', N'Electronics', 1, @SystemUserID);
        PRINT N'✅ Default Categories seeded.';
    END

    IF NOT EXISTS (SELECT 1 FROM dbo.PriceLists WHERE PriceListCode = 'DEFAULT' AND CompanyID = @SystemCompanyID)
    BEGIN
        INSERT INTO dbo.PriceLists (CompanyID, PriceListCode, PriceListNameAR, PriceListNameEN, CurrencyCode, IsDefault, IsActive, CreatedBy)
        VALUES (@SystemCompanyID, 'DEFAULT', N'قائمة الأسعار الافتراضية', N'Default Price List', 'SAR', 1, 1, @SystemUserID);
        PRINT N'✅ Default Price List seeded.';
    END

END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    DECLARE @ErrorMessage NVARCHAR(4000) = ERROR_MESSAGE();
    DECLARE @ErrorSeverity INT = ERROR_SEVERITY();
    RAISERROR(@ErrorMessage, @ErrorSeverity, 1);
END CATCH
GO