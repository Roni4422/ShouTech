-- ========================================================================
-- FILE: dba/mfg/route.sql
-- PROJECT: SHOUTECH ERP V10 - Routing & Capacity Extensions (Ultimate 10/10)
-- VERSION: 10.3.0
-- DESCRIPTION: Operation standard times with validity history and work
--              center capacity plans (finite scheduling input).
--              NOTE: dbo.ProductionRoutings + dbo.WorkCenters → dba/mfg/mfg.sql.
-- ========================================================================
-- ✅ Idempotent | TRY/CATCH + TRANSACTION | Soft Delete + RowVersion
-- ========================================================================

USE [$(DbFileName)];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

PRINT N'═══════════════════════════════════════════════════════════════════════════';
PRINT N'SHOUTECH ERP V10 – Routing & Capacity Extensions (Ultimate 10/10)';
PRINT N'═══════════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- ==========================================================================
    --    History-preserving like tax rates: new standard → new row.
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'OperationStandards')
    BEGIN
        CREATE TABLE dbo.OperationStandards (
            OperationStandardID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            RoutingID INT NOT NULL,                      -- Loose ref → ProductionRoutings
            OperationSeq INT NOT NULL,
            OperationName NVARCHAR(200) NOT NULL,
            WorkCenterID INT NULL,
            StandardSetupMinutes INT NOT NULL DEFAULT 0,
            StandardRunMinutesPerUnit DECIMAL(18,4) NOT NULL,
            MachineMinutesPerUnit DECIMAL(18,4) NOT NULL DEFAULT 0,
            LaborRatePerHour DECIMAL(18,4) NOT NULL DEFAULT 0,
            OverheadRatePerHour DECIMAL(18,4) NOT NULL DEFAULT 0,
            ExpectedScrapPct DECIMAL(5,2) NOT NULL DEFAULT 0,
            CrewSize DECIMAL(4,1) NOT NULL DEFAULT 1,
            ValidFrom DATE NOT NULL,
            ValidTo DATE NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_OperationStandards PRIMARY KEY CLUSTERED (OperationStandardID),
            CONSTRAINT CK_OpStandards_Run CHECK (StandardRunMinutesPerUnit > 0),
            CONSTRAINT CK_OpStandards_Validity CHECK (ValidTo IS NULL OR ValidTo >= ValidFrom)
        );
        CREATE NONCLUSTERED INDEX IX_OpStandards_Routing
            ON dbo.OperationStandards (CompanyID, RoutingID, OperationSeq, ValidFrom) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    --    Input for finite scheduling and load-vs-capacity reports.
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CapacityPlans')
    BEGIN
        CREATE TABLE dbo.CapacityPlans (
            CapacityPlanID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            WorkCenterID INT NOT NULL,
            PeriodYear INT NOT NULL,
            PeriodMonth TINYINT NOT NULL,
            WorkingDays TINYINT NOT NULL,
            ShiftsPerDay TINYINT NOT NULL DEFAULT 1,
            HoursPerShift DECIMAL(4,2) NOT NULL DEFAULT 8,
            EfficiencyPct DECIMAL(5,2) NOT NULL DEFAULT 85,  -- OEE efficiency assumption
            UtilizationPct DECIMAL(5,2) NOT NULL DEFAULT 90,
            PlannedCapacityHours AS (WorkingDays * ShiftsPerDay * HoursPerShift * EfficiencyPct / 100 * UtilizationPct / 100) PERSISTED,
            LoadedHours DECIMAL(18,2) NOT NULL DEFAULT 0,    -- Sum of scheduled operations
            Notes NVARCHAR(300) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_CapacityPlans PRIMARY KEY CLUSTERED (CapacityPlanID),
            CONSTRAINT UQ_CapacityPlans UNIQUE (CompanyID, WorkCenterID, PeriodYear, PeriodMonth),
            CONSTRAINT CK_CapacityPlans_Month CHECK (PeriodMonth BETWEEN 1 AND 12),
            CONSTRAINT CK_CapacityPlans_Pct CHECK (EfficiencyPct BETWEEN 0 AND 100 AND UtilizationPct BETWEEN 0 AND 100)
        );
        CREATE NONCLUSTERED INDEX IX_CapacityPlans_Period
            ON dbo.CapacityPlans (CompanyID, PeriodYear, PeriodMonth) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- COMPLETION
    -- ==========================================================================
    COMMIT TRANSACTION;
    PRINT N'═══════════════════════════════════════════════════════════════════════════';
    PRINT N'✅ Routing & Capacity Extensions deployed successfully.';
    PRINT N'   - Validity-based standards + computed capacity plans.';
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
