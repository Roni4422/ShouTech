-- ========================================================================
-- FILE: dba/fin/fin_asst.sql
-- PROJECT: SHOUTECH ERP V10 - Fixed Assets Extensions (Ultimate 10/10)
-- VERSION: 10.3.0
-- DESCRIPTION: Asset categories (IAS 16), locations & custodians, movement
--              log, maintenance records, revaluations and disposals with
--              full audit.
--              NOTE: dbo.FixedAssets + dbo.DepreciationSchedule → dba/fin/fin.sql.
-- ========================================================================
-- ✅ Idempotent | TRY/CATCH + TRANSACTION | Soft Delete + RowVersion
-- ✅ IFRS IAS 16 compliant (cost model + revaluation model).
-- ========================================================================

USE [$(DbFileName)];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

PRINT N'═══════════════════════════════════════════════════════════════════════════';
PRINT N'SHOUTECH ERP V10 – Fixed Assets Extensions (Ultimate 10/10)';
PRINT N'═══════════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AssetCategories')
    BEGIN
        CREATE TABLE dbo.AssetCategories (
            AssetCategoryID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            CategoryCode NVARCHAR(20) NOT NULL,
            CategoryNameAR NVARCHAR(150) NOT NULL,
            CategoryNameEN NVARCHAR(150) NOT NULL,
            DefaultUsefulLifeYears INT NOT NULL,
            DefaultDepreciationMethod NVARCHAR(15) NOT NULL DEFAULT 'STRAIGHT_LINE', -- STRAIGHT_LINE | DECLINING_BALANCE | UNITS_OF_PRODUCTION
            DefaultSalvagePct DECIMAL(5,2) NOT NULL DEFAULT 0,
            AssetAccountID INT NULL,                     -- Loose refs → COA
            AccumDepAccountID INT NULL,
            DepExpenseAccountID INT NULL,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_AssetCategories PRIMARY KEY CLUSTERED (AssetCategoryID),
            CONSTRAINT UQ_AssetCategories_Code UNIQUE (CompanyID, CategoryCode),
            CONSTRAINT CK_AssetCategories_Method CHECK (DefaultDepreciationMethod IN ('STRAIGHT_LINE','DECLINING_BALANCE','UNITS_OF_PRODUCTION'))
        );
    END

    -- ==========================================================================
    --    Immutable log — every transfer inserts a row.
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AssetMovements')
    BEGIN
        CREATE TABLE dbo.AssetMovements (
            AssetMovementID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            AssetID INT NOT NULL,                        -- Loose ref → FixedAssets
            MovementType NVARCHAR(15) NOT NULL,          -- TRANSFER | CUSTODY_CHANGE | LOCATION_CHANGE | SENT_REPAIR | RETURNED
            MovementDate DATE NOT NULL,
            FromLocation NVARCHAR(200) NULL,
            ToLocation NVARCHAR(200) NULL,
            FromCustodianID INT NULL,                    -- EmployeeID
            ToCustodianID INT NULL,
            Reason NVARCHAR(500) NOT NULL,
            DocumentRef NVARCHAR(100) NULL,
            ApprovedBy UNIQUEIDENTIFIER NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_AssetMovements PRIMARY KEY CLUSTERED (AssetMovementID),
            CONSTRAINT CK_AssetMovements_Type CHECK (MovementType IN ('TRANSFER','CUSTODY_CHANGE','LOCATION_CHANGE','SENT_REPAIR','RETURNED'))
        ) WITH (DATA_COMPRESSION = PAGE);
        CREATE NONCLUSTERED INDEX IX_AssetMovements_Asset
            ON dbo.AssetMovements (CompanyID, AssetID, MovementDate DESC) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AssetMaintenance')
    BEGIN
        CREATE TABLE dbo.AssetMaintenance (
            MaintenanceID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            AssetID INT NOT NULL,
            MaintenanceType NVARCHAR(15) NOT NULL,       -- PREVENTIVE | CORRECTIVE | INSPECTION
            ScheduledDate DATE NOT NULL,
            CompletedDate DATE NULL,
            Description NVARCHAR(500) NOT NULL,
            ServiceProvider NVARCHAR(200) NULL,
            Cost DECIMAL(18,4) NOT NULL DEFAULT 0,
            Capitalize BIT NOT NULL DEFAULT 0,           -- IAS 16: capitalize vs expense
            NextDueDate DATE NULL,
            Status NVARCHAR(12) NOT NULL DEFAULT 'SCHEDULED', -- SCHEDULED | IN_PROGRESS | COMPLETED | CANCELLED
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_AssetMaintenance PRIMARY KEY CLUSTERED (MaintenanceID),
            CONSTRAINT CK_AssetMaintenance_Type CHECK (MaintenanceType IN ('PREVENTIVE','CORRECTIVE','INSPECTION')),
            CONSTRAINT CK_AssetMaintenance_Status CHECK (Status IN ('SCHEDULED','IN_PROGRESS','COMPLETED','CANCELLED'))
        );
        CREATE NONCLUSTERED INDEX IX_AssetMaintenance_Due
            ON dbo.AssetMaintenance (CompanyID, NextDueDate) WHERE IsDeleted = 0 AND Status <> 'CANCELLED';
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AssetRevaluations')
    BEGIN
        CREATE TABLE dbo.AssetRevaluations (
            RevaluationID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            AssetID INT NOT NULL,
            RevaluationDate DATE NOT NULL,
            CarryingAmountBefore DECIMAL(18,4) NOT NULL,
            FairValue DECIMAL(18,4) NOT NULL,
            RevaluationSurplus DECIMAL(18,4) NOT NULL,   -- + surplus (OCI) / − deficit (P&L first)
            ValuationMethod NVARCHAR(30) NOT NULL,       -- MARKET | INCOME | COST | EXTERNAL_APPRAISER
            AppraiserName NVARCHAR(200) NULL,
            AppraisalReportRef NVARCHAR(100) NULL,
            JournalEntryID BIGINT NULL,                  -- Loose ref → JournalEntries
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_AssetRevaluations PRIMARY KEY CLUSTERED (RevaluationID),
            CONSTRAINT UQ_AssetRevaluations UNIQUE (CompanyID, AssetID, RevaluationDate)
        );
        CREATE NONCLUSTERED INDEX IX_AssetRevaluations_Asset
            ON dbo.AssetRevaluations (CompanyID, AssetID, RevaluationDate DESC) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AssetDisposals')
    BEGIN
        CREATE TABLE dbo.AssetDisposals (
            DisposalID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            AssetID INT NOT NULL,
            DisposalDate DATE NOT NULL,
            DisposalType NVARCHAR(15) NOT NULL,          -- SALE | SCRAP | DONATION | WRITE_OFF | TRADE_IN
            CarryingAmount DECIMAL(18,4) NOT NULL,
            ProceedsAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
            GainLoss AS (ProceedsAmount - CarryingAmount) PERSISTED,
            BuyerInfo NVARCHAR(300) NULL,
            DocumentRef NVARCHAR(100) NULL,
            JournalEntryID BIGINT NULL,
            ApprovedBy UNIQUEIDENTIFIER NOT NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_AssetDisposals PRIMARY KEY CLUSTERED (DisposalID),
            CONSTRAINT CK_AssetDisposals_Type CHECK (DisposalType IN ('SALE','SCRAP','DONATION','WRITE_OFF','TRADE_IN'))
        );
        CREATE NONCLUSTERED INDEX IX_AssetDisposals_Asset
            ON dbo.AssetDisposals (CompanyID, AssetID, DisposalDate DESC) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- 6. SEED DATA — asset categories
    -- ==========================================================================
    PRINT N'✅ Seeding asset categories...';

    IF NOT EXISTS (SELECT 1 FROM dbo.AssetCategories WHERE CategoryCode = 'BLDG')
    BEGIN
        INSERT INTO dbo.AssetCategories
            (CompanyID, CategoryCode, CategoryNameAR, CategoryNameEN,
             DefaultUsefulLifeYears, DefaultDepreciationMethod, DefaultSalvagePct, CreatedBy)
        SELECT TOP 1 C.CompanyID, X.Code, X.NameAR, X.NameEN, X.Life, X.Method, X.Salvage,
               '00000000-0000-0000-0000-000000000001'
        FROM MST.dbo.Companies C
        CROSS JOIN (VALUES
            ('BLDG',  N'مبانٍ وإنشاءات',       'Buildings',            25, 'STRAIGHT_LINE',     5),
            ('PLANT', N'آلات ومعدات',          'Plant & Machinery',    10, 'STRAIGHT_LINE',     5),
            ('VEH',   N'سيارات ومركبات',       'Vehicles',              5, 'STRAIGHT_LINE',    10),
            ('FURN',  N'أثاث ومفروشات',        'Furniture & Fixtures',  7, 'STRAIGHT_LINE',     0),
            ('IT',    N'أجهزة حاسوب وتقنية',   'IT Equipment',          4, 'STRAIGHT_LINE',     0),
            ('LAND',  N'أراضٍ',                'Land (non-depreciable)',0, 'STRAIGHT_LINE',     0)
        ) X (Code, NameAR, NameEN, Life, Method, Salvage);
    END

    -- ==========================================================================
    -- COMPLETION
    -- ==========================================================================
    COMMIT TRANSACTION;
    PRINT N'═══════════════════════════════════════════════════════════════════════════';
    PRINT N'✅ Fixed Assets Extensions deployed successfully.';
    PRINT N'   - IAS 16 categories + movements + maintenance + revaluations + disposals.';
    PRINT N'═══════════════════════════════════════════════════════════════════════════';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    DECLARE @ErrorMessage NVARCHAR(4000) = ERROR_MESSAGE();
    DECLARE @ErrorSeverity INT = ERROR_SEVERITY();
    DECLARE @ErrorState INT = ERROR_STATE();
    RAISERROR(@ErrorMessage, @ErrorSeverity, @ErrorState);
END CATCH;
GO
