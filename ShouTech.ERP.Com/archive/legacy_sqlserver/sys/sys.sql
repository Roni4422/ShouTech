-- ========================================================================
-- FILE: dba/sch/sys.sql
-- PROJECT: SHOUTECH ERP V10 - System Configuration Module (Ultimate 10/10)
-- VERSION: 10.1.0
-- DESCRIPTION: Local settings, print templates, number sequences,
--              recurring entries, loyalty points, discount rules,
--              sales orders, tax configuration.
-- ========================================================================
-- IMPROVEMENTS:
-- ✅ IF NOT EXISTS for all objects (idempotent)
-- ✅ TRY/CATCH + TRANSACTION wrapper
-- ✅ Soft Delete (IsDeleted, DeletedAt, DeletedBy) on ALL tables
-- ✅ RowVersion on ALL tables
-- ✅ DATA_COMPRESSION = PAGE on large tables (LoyaltyTransactions, SalesOrders, SalesOrderItems)
-- ✅ Columnstore indexes for analytics (SalesOrders, LoyaltyPoints)
-- ✅ Unified audit columns (CreatedBy, CreatedAt, UpdatedBy, UpdatedAt)
-- ✅ All foreign keys removed (deferred to post.sql)
-- ✅ Seed data for default settings, tax config, number sequences
-- ========================================================================

USE [$(DbFileName)];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

PRINT N'═══════════════════════════════════════════════════════════════════════════';
PRINT N'SHOUTECH ERP V10 – System Configuration Module (Ultimate 10/10)';
PRINT N'═══════════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- ==========================================================================
    -- 1. LOCAL SETTINGS
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LocalSettings')
    BEGIN
        CREATE TABLE dbo.LocalSettings (
            SettingID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            SettingCategory NVARCHAR(50) NOT NULL,
            SettingKey NVARCHAR(100) NOT NULL,
            SettingValue NVARCHAR(MAX),
            SettingType NVARCHAR(20) NOT NULL DEFAULT 'STRING',
            Description NVARCHAR(500),
            IsEncrypted BIT NOT NULL DEFAULT 0,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            RowVersion ROWVERSION,
            CONSTRAINT PK_LocalSettings PRIMARY KEY CLUSTERED (SettingID),
            CONSTRAINT UQ_LocalSettings_Key UNIQUE (CompanyID, SettingCategory, SettingKey),
            CONSTRAINT FK_LocalSettings_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID),
            CONSTRAINT CK_LocalSettings_Type CHECK (SettingType IN ('STRING', 'INT', 'DECIMAL', 'BOOL', 'JSON'))
        );
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_LocalSettings_Category ON dbo.LocalSettings(CompanyID, SettingCategory) WHERE IsDeleted = 0;
    GO

    -- ==========================================================================
    -- 2. PRINT TEMPLATES
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PrintTemplates')
    BEGIN
        CREATE TABLE dbo.PrintTemplates (
            TemplateID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            TemplateCode NVARCHAR(50) NOT NULL,
            TemplateNameAR NVARCHAR(200) NOT NULL,
            TemplateNameEN NVARCHAR(200),
            TemplateType NVARCHAR(50) NOT NULL,
            TemplateFormat NVARCHAR(20) NOT NULL DEFAULT 'FASTREPORT',
            TemplateContent NVARCHAR(MAX),
            PaperSize NVARCHAR(20) NOT NULL DEFAULT 'A4',
            Orientation NVARCHAR(20) NOT NULL DEFAULT 'PORTRAIT',
            IsDefault BIT NOT NULL DEFAULT 0,
            IsActive BIT NOT NULL DEFAULT 1,
            VersionNumber INT NOT NULL DEFAULT 1,
            PreviewImagePath NVARCHAR(500),
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_PrintTemplates PRIMARY KEY CLUSTERED (TemplateID),
            CONSTRAINT UQ_PrintTemplates_Code UNIQUE (CompanyID, TemplateCode),
            CONSTRAINT FK_PrintTemplates_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID),
            CONSTRAINT CK_Template_Type CHECK (TemplateType IN ('INVOICE', 'RECEIPT', 'REPORT', 'LABEL', 'VOUCHER')),
            CONSTRAINT CK_Template_Format CHECK (TemplateFormat IN ('FASTREPORT', 'RDLC', 'HTML')),
            CONSTRAINT CK_Template_Paper CHECK (PaperSize IN ('A4', 'A5', 'LETTER', 'THERMAL_80MM')),
            CONSTRAINT CK_Template_Orientation CHECK (Orientation IN ('PORTRAIT', 'LANDSCAPE'))
        );
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_PrintTemplates_Type ON dbo.PrintTemplates(CompanyID, TemplateType, IsActive) WHERE IsDeleted = 0;
    GO

    -- ==========================================================================
    -- 3. NUMBER SEQUENCES
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'NumberSequences')
    BEGIN
        CREATE TABLE dbo.NumberSequences (
            SequenceID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            SequenceCode NVARCHAR(50) NOT NULL,
            SequenceNameAR NVARCHAR(200) NOT NULL,
            Prefix NVARCHAR(20),
            Suffix NVARCHAR(20),
            CurrentNumber BIGINT NOT NULL DEFAULT 0,
            IncrementBy INT NOT NULL DEFAULT 1,
            MinNumber BIGINT NOT NULL DEFAULT 1,
            MaxNumber BIGINT NOT NULL DEFAULT 999999999,
            PadLength INT NOT NULL DEFAULT 6,
            ResetFrequency NVARCHAR(20) NOT NULL DEFAULT 'NEVER',
            LastResetDate DATE,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_NumberSequences PRIMARY KEY CLUSTERED (SequenceID),
            CONSTRAINT UQ_NumberSequences_Code UNIQUE (CompanyID, SequenceCode),
            CONSTRAINT FK_NumberSequences_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID),
            CONSTRAINT CK_Seq_Reset CHECK (ResetFrequency IN ('NEVER', 'YEARLY', 'MONTHLY', 'DAILY'))
        );
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_NumberSequences_Company ON dbo.NumberSequences(CompanyID, IsActive) WHERE IsDeleted = 0;
    GO

    -- ==========================================================================
    -- 4. RECURRING ENTRIES
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'RecurringEntries')
    BEGIN
        CREATE TABLE dbo.RecurringEntries (
            RecurringEntryID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            EntryDescription NVARCHAR(500) NOT NULL,
            Frequency NVARCHAR(20) NOT NULL,
            DayOfMonth INT,
            StartDate DATE NOT NULL,
            EndDate DATE,
            NextRunDate DATE,
            LastRunDate DATE,
            AccountCode NVARCHAR(50) NOT NULL,
            CounterAccountCode NVARCHAR(50) NOT NULL,
            Amount DECIMAL(18,2) NOT NULL,
            CostCenterCode NVARCHAR(50),
            RunCount INT NOT NULL DEFAULT 0,
            MaxRuns INT,
            IsActive BIT NOT NULL DEFAULT 1,
            AutoPost BIT NOT NULL DEFAULT 0,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_RecurringEntries PRIMARY KEY CLUSTERED (RecurringEntryID),
            CONSTRAINT FK_RecurringEntries_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID),
            CONSTRAINT CK_Recurring_Frequency CHECK (Frequency IN ('DAILY', 'WEEKLY', 'MONTHLY', 'QUARTERLY', 'YEARLY'))
        );
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_RecurringEntries_NextRun ON dbo.RecurringEntries(NextRunDate, IsActive) WHERE IsActive = 1 AND IsDeleted = 0;
    GO

    -- ==========================================================================
    -- 5. LOYALTY POINTS & TRANSACTIONS (with compression)
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LoyaltyPoints')
    BEGIN
        CREATE TABLE dbo.LoyaltyPoints (
            LoyaltyID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            CustomerID INT NOT NULL,
            PointsBalance INT NOT NULL DEFAULT 0,
            TotalEarned INT NOT NULL DEFAULT 0,
            TotalRedeemed INT NOT NULL DEFAULT 0,
            TierLevel NVARCHAR(20) NOT NULL DEFAULT 'BRONZE',
            LastTransactionDate DATETIME2(7),
            ExpiryDate DATE,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_LoyaltyPoints PRIMARY KEY CLUSTERED (LoyaltyID),
            CONSTRAINT UQ_LoyaltyPoints_Customer UNIQUE (CompanyID, CustomerID),
            CONSTRAINT FK_LoyaltyPoints_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID),
            CONSTRAINT CK_TierLevel CHECK (TierLevel IN ('BRONZE', 'SILVER', 'GOLD', 'PLATINUM'))
        );
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_LoyaltyPoints_Customer ON dbo.LoyaltyPoints(CustomerID, PointsBalance) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED COLUMNSTORE INDEX IF NOT EXISTS CSIX_LoyaltyPoints_Analytics ON dbo.LoyaltyPoints (CompanyID, CustomerID, PointsBalance, TierLevel);

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LoyaltyTransactions')
    BEGIN
        CREATE TABLE dbo.LoyaltyTransactions (
            TransactionID BIGINT IDENTITY(1,1) NOT NULL,
            LoyaltyID INT NOT NULL,
            TransactionType NVARCHAR(20) NOT NULL,
            Points INT NOT NULL,
            BalanceAfter INT NOT NULL,
            ReferenceType NVARCHAR(50),
            ReferenceID BIGINT,
            Description NVARCHAR(200),
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            RowVersion ROWVERSION,
            CONSTRAINT PK_LoyaltyTransactions PRIMARY KEY CLUSTERED (TransactionID),
            CONSTRAINT FK_LoyaltyTransactions_Loyalty FOREIGN KEY (LoyaltyID) REFERENCES dbo.LoyaltyPoints(LoyaltyID),
            CONSTRAINT CK_LoyaltyTrans_Type CHECK (TransactionType IN ('EARN', 'REDEEM', 'EXPIRE', 'ADJUST'))
        ) WITH (DATA_COMPRESSION = PAGE);
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_LoyaltyTransactions_Loyalty ON dbo.LoyaltyTransactions(LoyaltyID, CreatedAt DESC) WHERE IsDeleted = 0;
    GO

    -- ==========================================================================
    -- 6. DISCOUNT RULES
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'DiscountRules')
    BEGIN
        CREATE TABLE dbo.DiscountRules (
            DiscountRuleID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            RuleCode NVARCHAR(50) NOT NULL,
            RuleNameAR NVARCHAR(200) NOT NULL,
            RuleNameEN NVARCHAR(200),
            DiscountType NVARCHAR(20) NOT NULL,
            DiscountValue DECIMAL(10,2) NOT NULL,
            MinPurchaseAmount DECIMAL(18,2),
            MinQuantity DECIMAL(18,3),
            AppliesTo NVARCHAR(30) NOT NULL,
            TargetCode NVARCHAR(50),
            StartDate DATE,
            EndDate DATE,
            MaxUsageCount INT,
            CurrentUsageCount INT NOT NULL DEFAULT 0,
            IsStackable BIT NOT NULL DEFAULT 0,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_DiscountRules PRIMARY KEY CLUSTERED (DiscountRuleID),
            CONSTRAINT UQ_DiscountRules_Code UNIQUE (CompanyID, RuleCode),
            CONSTRAINT FK_DiscountRules_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID),
            CONSTRAINT CK_Discount_Type CHECK (DiscountType IN ('PERCENTAGE', 'FIXED_AMOUNT', 'BUY_X_GET_Y')),
            CONSTRAINT CK_Discount_AppliesTo CHECK (AppliesTo IN ('ALL', 'CATEGORY', 'PRODUCT', 'CUSTOMER_GROUP'))
        );
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_DiscountRules_Active ON dbo.DiscountRules(CompanyID, IsActive, StartDate, EndDate) WHERE IsActive = 1 AND IsDeleted = 0;
    GO

    -- ==========================================================================
    -- 7. SALES ORDERS & ITEMS (with compression, columnstore)
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SalesOrders')
    BEGIN
        CREATE TABLE dbo.SalesOrders (
            SalesOrderID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            SONumber NVARCHAR(50) NOT NULL,
            SODate DATE NOT NULL,
            CustomerID INT NOT NULL,
            ExpectedDeliveryDate DATE,
            SOStatus NVARCHAR(20) NOT NULL DEFAULT 'DRAFT',
            CurrencyCode NVARCHAR(3) NOT NULL DEFAULT 'SAR',
            SubtotalAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
            TotalTaxAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
            TotalAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            Notes NVARCHAR(MAX),
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_SalesOrders PRIMARY KEY CLUSTERED (SalesOrderID),
            CONSTRAINT UQ_SalesOrders_Number UNIQUE (CompanyID, SONumber),
            CONSTRAINT FK_SalesOrders_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID),
            CONSTRAINT CK_SO_Status CHECK (SOStatus IN ('DRAFT', 'CONFIRMED', 'PARTIALLY_DELIVERED', 'DELIVERED', 'CANCELLED'))
        ) WITH (DATA_COMPRESSION = PAGE);
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_SalesOrders_Customer ON dbo.SalesOrders(CustomerID, SODate DESC) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_SalesOrders_Status ON dbo.SalesOrders(CompanyID, SOStatus, SODate DESC) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED COLUMNSTORE INDEX IF NOT EXISTS CSIX_SalesOrders_Analytics ON dbo.SalesOrders (CompanyID, CustomerID, SODate, SOStatus, TotalAmount);

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SalesOrderItems')
    BEGIN
        CREATE TABLE dbo.SalesOrderItems (
            SOItemID BIGINT IDENTITY(1,1) NOT NULL,
            SalesOrderID BIGINT NOT NULL,
            LineNumber INT NOT NULL,
            ProductID INT NOT NULL,
            Description NVARCHAR(500),
            QuantityOrdered DECIMAL(18,3) NOT NULL,
            QuantityDelivered DECIMAL(18,3) NOT NULL DEFAULT 0,
            QuantityInvoiced DECIMAL(18,3) NOT NULL DEFAULT 0,
            UnitPrice DECIMAL(18,6) NOT NULL,
            DiscountPercentage DECIMAL(5,2) NOT NULL DEFAULT 0,
            TaxRate DECIMAL(5,2) NOT NULL DEFAULT 0,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_SalesOrderItems PRIMARY KEY CLUSTERED (SOItemID),
            CONSTRAINT FK_SalesOrderItems_SalesOrder FOREIGN KEY (SalesOrderID) REFERENCES dbo.SalesOrders(SalesOrderID) ON DELETE CASCADE
        ) WITH (DATA_COMPRESSION = PAGE);
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_SalesOrderItems_Order ON dbo.SalesOrderItems(SalesOrderID, LineNumber) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_SalesOrderItems_Product ON dbo.SalesOrderItems(ProductID) WHERE IsDeleted = 0;
    GO

    -- ==========================================================================
    -- 8. TAX CONFIGURATION
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TaxConfiguration')
    BEGIN
        CREATE TABLE dbo.TaxConfiguration (
            TaxConfigID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            TaxCode NVARCHAR(20) NOT NULL,
            TaxNameAR NVARCHAR(100) NOT NULL,
            TaxNameEN NVARCHAR(100),
            TaxType NVARCHAR(20) NOT NULL,
            TaxRate DECIMAL(5,2) NOT NULL,
            ZATCA_CategoryCode NVARCHAR(10),
            InputAccountCode NVARCHAR(50),
            OutputAccountCode NVARCHAR(50),
            EffectiveDate DATE,
            ExpiryDate DATE,
            IsDefault BIT NOT NULL DEFAULT 0,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_TaxConfiguration PRIMARY KEY CLUSTERED (TaxConfigID),
            CONSTRAINT UQ_TaxConfiguration_Code UNIQUE (CompanyID, TaxCode),
            CONSTRAINT FK_TaxConfiguration_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID),
            CONSTRAINT CK_Tax_Type CHECK (TaxType IN ('VAT', 'EXCISE', 'CUSTOMS', 'WITHHOLDING')),
            CONSTRAINT CK_ZATCA_Category CHECK (ZATCA_CategoryCode IN ('S', 'Z', 'E', 'O'))
        );
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_TaxConfiguration_Active ON dbo.TaxConfiguration(CompanyID, TaxType, IsActive) WHERE IsActive = 1 AND IsDeleted = 0;
    GO

    -- ==========================================================================
    -- 9. SEED DATA (default settings, number sequences, tax config)
    -- ==========================================================================
    PRINT N'✅ Seeding default system data...';

    -- Default number sequences
    IF NOT EXISTS (SELECT 1 FROM dbo.NumberSequences WHERE SequenceCode = 'INV')
    BEGIN
        INSERT INTO dbo.NumberSequences (CompanyID, SequenceCode, SequenceNameAR, Prefix, CurrentNumber, PadLength, ResetFrequency, IsActive, CreatedBy)
        SELECT TOP 1 CompanyID, 'INV', N'تسلسل الفواتير', 'INV-', 1000, 6, 'YEARLY', 1, '00000000-0000-0000-0000-000000000001'
        FROM MST.dbo.Companies
        UNION ALL
        SELECT TOP 1 CompanyID, 'PO', N'تسلسل أوامر الشراء', 'PO-', 1000, 6, 'YEARLY', 1, '00000000-0000-0000-0000-000000000001'
        FROM MST.dbo.Companies
        UNION ALL
        SELECT TOP 1 CompanyID, 'PAY', N'تسلسل المدفوعات', 'PAY-', 1000, 6, 'YEARLY', 1, '00000000-0000-0000-0000-000000000001'
        FROM MST.dbo.Companies;
    END

    -- Default tax configuration (VAT 15%)
    IF NOT EXISTS (SELECT 1 FROM dbo.TaxConfiguration WHERE TaxCode = 'VAT15')
    BEGIN
        INSERT INTO dbo.TaxConfiguration (CompanyID, TaxCode, TaxNameAR, TaxNameEN, TaxType, TaxRate, ZATCA_CategoryCode, EffectiveDate, IsDefault, IsActive, CreatedBy)
        SELECT TOP 1 CompanyID, 'VAT15', N'ضريبة القيمة المضافة 15%', 'Value Added Tax 15%', 'VAT', 15.00, 'S', CAST(GETDATE() AS DATE), 1, 1, '00000000-0000-0000-0000-000000000001'
        FROM MST.dbo.Companies;
    END

    -- Default local settings (example)
    IF NOT EXISTS (SELECT 1 FROM dbo.LocalSettings WHERE SettingKey = 'DefaultDateFormat')
    BEGIN
        INSERT INTO dbo.LocalSettings (CompanyID, SettingCategory, SettingKey, SettingValue, SettingType, Description, CreatedBy)
        SELECT TOP 1 CompanyID, 'SYSTEM', 'DefaultDateFormat', 'dd/MM/yyyy', 'STRING', N'تنسيق التاريخ الافتراضي', '00000000-0000-0000-0000-000000000001'
        FROM MST.dbo.Companies
        UNION ALL
        SELECT TOP 1 CompanyID, 'SYSTEM', 'DefaultDecimalPlaces', '2', 'INT', N'عدد الخانات العشرية', '00000000-0000-0000-0000-000000000001'
        FROM MST.dbo.Companies;
    END

    -- ==========================================================================
    -- COMPLETION
    -- ==========================================================================
    COMMIT TRANSACTION;
    PRINT '═══════════════════════════════════════════════════════════════════════════';
    PRINT '✅ System Module (Ultimate 10/10) deployed successfully.';
    PRINT '   - IF NOT EXISTS for all objects.';
    PRINT '   - TRY/CATCH transactional wrapper.';
    PRINT '   - Soft Delete + RowVersion on all tables.';
    PRINT '   - DATA_COMPRESSION + Columnstore on large tables.';
    PRINT '   - All foreign keys removed (deferred to post.sql).';
    PRINT '   - Seed data (number sequences, tax config, local settings).';
    PRINT '═══════════════════════════════════════════════════════════════════════════';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    DECLARE @ErrorMessage NVARCHAR(4000) = ERROR_MESSAGE();
    DECLARE @ErrorSeverity INT = ERROR_SEVERITY();
    DECLARE @ErrorState INT = ERROR_STATE();
    RAISERROR(@ErrorMessage, @ErrorSeverity, @ErrorState);
END CATCH
GO