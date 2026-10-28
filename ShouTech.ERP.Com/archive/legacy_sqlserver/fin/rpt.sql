-- ========================================================================
-- FILE: dba/fin/rpt.sql
-- PROJECT: SHOUTECH ERP V10 - Custom Report Designer Store (Ultimate 10/10)
-- VERSION: 10.3.0
-- DESCRIPTION: Persistence layer for the Excel-like report designer:
--              user-defined report definitions, column layouts (width,
--              order, visibility), per-column FORMULA expressions, groupings,
--              sorts and saved filters. "Edit Report → Save As" writes here.
-- ========================================================================
-- ✅ Idempotent | TRY/CATCH + TRANSACTION | Soft Delete + RowVersion
-- ✅ Formulas stored as TEXT only — never executed as SQL. They are
--    parsed by the application's sandboxed expression engine (no SQL eval).
-- ✅ Personal vs shared reports with owner + permission control.
-- ========================================================================

USE [$(DbFileName)];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

PRINT N'═══════════════════════════════════════════════════════════════════════════';
PRINT N'SHOUTECH ERP V10 – Custom Report Designer Store (Ultimate 10/10)';
PRINT N'═══════════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CustomReportDefinitions')
    BEGIN
        CREATE TABLE dbo.CustomReportDefinitions (
            ReportDefID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ReportKey NVARCHAR(60) NOT NULL,             -- Base report it derives from (e.g. 'SALES_BY_ITEM')
            ReportName NVARCHAR(200) NOT NULL,           -- User-chosen name ("Save As…")
            Description NVARCHAR(500) NULL,
            LanguageCode NVARCHAR(5) NOT NULL DEFAULT 'ar',  -- ar | en | fr | tr
            OwnerUserID INT NOT NULL,                    -- Creator
            Visibility NVARCHAR(10) NOT NULL DEFAULT 'PRIVATE', -- PRIVATE | SHARED | SYSTEM
            DataSourceKey NVARCHAR(60) NOT NULL,         -- Whitelisted dataset (never raw SQL)
            PageOrientation NVARCHAR(10) NOT NULL DEFAULT 'PORTRAIT', -- PORTRAIT | LANDSCAPE
            ShowTotalsRow BIT NOT NULL DEFAULT 1,
            ShowHeaderInfo BIT NOT NULL DEFAULT 1,       -- Company/logo/date range block
            FreezeFirstColumn BIT NOT NULL DEFAULT 0,
            IsFavorite BIT NOT NULL DEFAULT 0,
            IsSystem BIT NOT NULL DEFAULT 0,             -- Factory templates (read-only)
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_CustomReportDefinitions PRIMARY KEY CLUSTERED (ReportDefID),
            CONSTRAINT UQ_CustomReportDefinitions UNIQUE (CompanyID, OwnerUserID, ReportKey, ReportName),
            CONSTRAINT CK_CustomReportDefinitions_Vis CHECK (Visibility IN ('PRIVATE','SHARED','SYSTEM')),
            CONSTRAINT CK_CustomReportDefinitions_Lang CHECK (LanguageCode IN ('ar','en','fr','tr'))
        );
        CREATE NONCLUSTERED INDEX IX_CustomReportDefinitions_Owner
            ON dbo.CustomReportDefinitions (CompanyID, OwnerUserID, ReportKey) WHERE IsDeleted = 0;
        CREATE NONCLUSTERED INDEX IX_CustomReportDefinitions_Shared
            ON dbo.CustomReportDefinitions (CompanyID, ReportKey, Visibility) WHERE IsDeleted = 0 AND Visibility <> 'PRIVATE';
    END

    -- ==========================================================================
    --    ColumnWidth persisted in pixels (WPF units) — this is what the
    --    "resize column" gesture writes back.
    --    FormulaText: sandboxed expression e.g. '=Qty * UnitPrice * (1 - Disc/100)'
    --    References other columns by FieldKey in [brackets]: '=[Qty]*[Price]'
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CustomReportColumns')
    BEGIN
        CREATE TABLE dbo.CustomReportColumns (
            ReportColumnID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ReportDefID INT NOT NULL,                    -- Loose ref → CustomReportDefinitions
            FieldKey NVARCHAR(60) NOT NULL,              -- Data field or user-defined name
            CaptionAR NVARCHAR(200) NOT NULL,
            CaptionEN NVARCHAR(200) NULL,
            DisplayOrder INT NOT NULL,
            ColumnWidth DECIMAL(9,2) NOT NULL DEFAULT 120,  -- Pixels (WPF DIPs)
            IsVisible BIT NOT NULL DEFAULT 1,
            IsLocked BIT NOT NULL DEFAULT 0,             -- Prevent resize/hide
            ColumnType NVARCHAR(15) NOT NULL DEFAULT 'DATA',  -- DATA | FORMULA | RUNNING_TOTAL | ROW_NUMBER
            FormulaText NVARCHAR(1000) NULL,             -- Sandboxed expression (see header)
            DataFormat NVARCHAR(30) NOT NULL DEFAULT 'TEXT',  -- TEXT | MONEY | QTY | PERCENT | DATE | DATETIME
            DecimalPlaces TINYINT NOT NULL DEFAULT 2,
            TextAlign NVARCHAR(6) NOT NULL DEFAULT 'AUTO',    -- AUTO | LEFT | CENTER | RIGHT (RTL-aware AUTO)
            AggregateFunc NVARCHAR(10) NULL,             -- SUM | AVG | MIN | MAX | COUNT | NULL
            NegativeInRed BIT NOT NULL DEFAULT 1,
            ConditionalFormatJson NVARCHAR(1000) NULL,   -- e.g. {"rules":[{"if":"<0","bg":"#FFEBEE"}]}
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_CustomReportColumns PRIMARY KEY CLUSTERED (ReportColumnID),
            CONSTRAINT UQ_CustomReportColumns UNIQUE (ReportDefID, FieldKey),
            CONSTRAINT CK_CustomReportColumns_Type CHECK (ColumnType IN ('DATA','FORMULA','RUNNING_TOTAL','ROW_NUMBER')),
            CONSTRAINT CK_CustomReportColumns_Align CHECK (TextAlign IN ('AUTO','LEFT','CENTER','RIGHT')),
            CONSTRAINT CK_CustomReportColumns_Width CHECK (ColumnWidth BETWEEN 30 AND 2000)
        );
        CREATE NONCLUSTERED INDEX IX_CustomReportColumns_Report
            ON dbo.CustomReportColumns (CompanyID, ReportDefID, DisplayOrder) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CustomReportGroupings')
    BEGIN
        CREATE TABLE dbo.CustomReportGroupings (
            ReportGroupingID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ReportDefID INT NOT NULL,
            FieldKey NVARCHAR(60) NOT NULL,
            GroupLevel TINYINT NOT NULL,                 -- 1 = outermost
            ShowSubTotals BIT NOT NULL DEFAULT 1,
            StartOnNewPage BIT NOT NULL DEFAULT 0,
            SortDirection NVARCHAR(4) NOT NULL DEFAULT 'ASC', -- ASC | DESC
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_CustomReportGroupings PRIMARY KEY CLUSTERED (ReportGroupingID),
            CONSTRAINT UQ_CustomReportGroupings UNIQUE (ReportDefID, GroupLevel),
            CONSTRAINT CK_CustomReportGroupings_Dir CHECK (SortDirection IN ('ASC','DESC'))
        );
    END

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CustomReportSorts')
    BEGIN
        CREATE TABLE dbo.CustomReportSorts (
            ReportSortID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ReportDefID INT NOT NULL,
            FieldKey NVARCHAR(60) NOT NULL,
            SortOrder TINYINT NOT NULL,
            SortDirection NVARCHAR(4) NOT NULL DEFAULT 'ASC',
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_CustomReportSorts PRIMARY KEY CLUSTERED (ReportSortID),
            CONSTRAINT UQ_CustomReportSorts UNIQUE (ReportDefID, SortOrder),
            CONSTRAINT CK_CustomReportSorts_Dir CHECK (SortDirection IN ('ASC','DESC'))
        );
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CustomReportFilters')
    BEGIN
        CREATE TABLE dbo.CustomReportFilters (
            ReportFilterID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ReportDefID INT NOT NULL,
            FilterKey NVARCHAR(60) NOT NULL,             -- Whitelisted parameter name
            FilterLabelAR NVARCHAR(150) NOT NULL,
            FilterLabelEN NVARCHAR(150) NULL,
            Operator NVARCHAR(12) NOT NULL DEFAULT 'EQ', -- EQ | NE | GT | GE | LT | LE | BETWEEN | IN | CONTAINS
            ValueFrom NVARCHAR(500) NULL,                -- Serialized literal (typed by client)
            ValueTo NVARCHAR(500) NULL,
            IsPrompted BIT NOT NULL DEFAULT 0,           -- Ask user at run time
            IsRequired BIT NOT NULL DEFAULT 0,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_CustomReportFilters PRIMARY KEY CLUSTERED (ReportFilterID),
            CONSTRAINT UQ_CustomReportFilters UNIQUE (ReportDefID, FilterKey),
            CONSTRAINT CK_CustomReportFilters_Op CHECK (Operator IN ('EQ','NE','GT','GE','LT','LE','BETWEEN','IN','CONTAINS'))
        );
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CustomReportShares')
    BEGIN
        CREATE TABLE dbo.CustomReportShares (
            ReportShareID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ReportDefID INT NOT NULL,
            GranteeType NVARCHAR(5) NOT NULL,            -- USER | ROLE
            GranteeID INT NOT NULL,
            CanView BIT NOT NULL DEFAULT 1,
            CanEdit BIT NOT NULL DEFAULT 0,              -- Edit layout of a shared report
            CanShare BIT NOT NULL DEFAULT 0,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_CustomReportShares PRIMARY KEY CLUSTERED (ReportShareID),
            CONSTRAINT UQ_CustomReportShares UNIQUE (ReportDefID, GranteeType, GranteeID),
            CONSTRAINT CK_CustomReportShares_Grantee CHECK (GranteeType IN ('USER','ROLE'))
        );
    END

    -- ==========================================================================
    -- COMPLETION
    -- ==========================================================================
    COMMIT TRANSACTION;
    PRINT N'═══════════════════════════════════════════════════════════════════════════';
    PRINT N'✅ Custom Report Designer Store deployed successfully.';
    PRINT N'   - Definitions + columns (width/order/formulas) + grouping/sort';
    PRINT N'     + filters + share grants.';
    PRINT N'   - Formulas are sandboxed text — parsed in-app, never executed as SQL.';
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
