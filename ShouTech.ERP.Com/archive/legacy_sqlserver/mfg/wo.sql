-- ========================================================================
-- FILE: dba/mfg/wo.sql
-- PROJECT: SHOUTECH ERP V10 - Work Order Execution Extensions (Ultimate 10/10)
-- VERSION: 10.3.0
-- DESCRIPTION: Shop-floor execution: operation-level tracking (start/stop),
--              labor time logs and downtime reasons.
--              NOTE: dbo.WorkOrders + dbo.MaterialFlows → dba/mfg/mfg.sql.
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
PRINT N'SHOUTECH ERP V10 – Work Order Execution Extensions (Ultimate 10/10)';
PRINT N'═══════════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WorkOrderOperations')
    BEGIN
        CREATE TABLE dbo.WorkOrderOperations (
            WoOperationID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            WorkOrderID BIGINT NOT NULL,                 -- Loose ref → WorkOrders
            OperationSeq INT NOT NULL,                   -- 10, 20, 30…
            OperationName NVARCHAR(200) NOT NULL,
            WorkCenterID INT NULL,                       -- Loose ref → WorkCenters
            PlannedSetupMinutes INT NOT NULL DEFAULT 0,
            PlannedRunMinutes DECIMAL(18,2) NOT NULL DEFAULT 0,
            ActualSetupMinutes INT NULL,
            ActualRunMinutes DECIMAL(18,2) NULL,
            QtyPlanned DECIMAL(18,4) NOT NULL,
            QtyCompleted DECIMAL(18,4) NOT NULL DEFAULT 0,
            QtyScrapped DECIMAL(18,4) NOT NULL DEFAULT 0,
            QtyReworked DECIMAL(18,4) NOT NULL DEFAULT 0,
            OperatorEmployeeID INT NULL,
            StartedAt DATETIME2(7) NULL,
            CompletedAt DATETIME2(7) NULL,
            Status NVARCHAR(12) NOT NULL DEFAULT 'PENDING', -- PENDING | RUNNING | PAUSED | COMPLETED | SKIPPED
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_WorkOrderOperations PRIMARY KEY CLUSTERED (WoOperationID),
            CONSTRAINT UQ_WorkOrderOperations UNIQUE (WorkOrderID, OperationSeq),
            CONSTRAINT CK_WoOperations_Status CHECK (Status IN ('PENDING','RUNNING','PAUSED','COMPLETED','SKIPPED')),
            CONSTRAINT CK_WoOperations_Qty CHECK (QtyCompleted >= 0 AND QtyScrapped >= 0 AND QtyCompleted + QtyScrapped <= QtyPlanned * 1.2)
        ) WITH (DATA_COMPRESSION = PAGE);
        CREATE NONCLUSTERED INDEX IX_WoOperations_WO
            ON dbo.WorkOrderOperations (CompanyID, WorkOrderID, OperationSeq) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WorkOrderLaborLogs')
    BEGIN
        CREATE TABLE dbo.WorkOrderLaborLogs (
            LaborLogID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            WoOperationID BIGINT NOT NULL,               -- Loose ref → WorkOrderOperations
            EmployeeID INT NOT NULL,
            LogDate DATE NOT NULL,
            StartTime DATETIME2(7) NOT NULL,
            EndTime DATETIME2(7) NULL,                   -- NULL = still running
            WorkedMinutes AS (CASE WHEN EndTime IS NOT NULL
                                   THEN DATEDIFF(MINUTE, StartTime, EndTime) END) PERSISTED,
            HourlyRate DECIMAL(18,4) NULL,               -- Snapshot at log time
            IsOvertime BIT NOT NULL DEFAULT 0,
            Notes NVARCHAR(300) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_WorkOrderLaborLogs PRIMARY KEY CLUSTERED (LaborLogID),
            CONSTRAINT CK_WoLaborLogs_Time CHECK (EndTime IS NULL OR EndTime > StartTime)
        ) WITH (DATA_COMPRESSION = PAGE);
        CREATE NONCLUSTERED INDEX IX_WoLaborLogs_Op
            ON dbo.WorkOrderLaborLogs (CompanyID, WoOperationID, LogDate) WHERE IsDeleted = 0;
        CREATE NONCLUSTERED INDEX IX_WoLaborLogs_Emp
            ON dbo.WorkOrderLaborLogs (CompanyID, EmployeeID, LogDate) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WorkOrderDowntimeLogs')
    BEGIN
        CREATE TABLE dbo.WorkOrderDowntimeLogs (
            DowntimeLogID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            WoOperationID BIGINT NOT NULL,
            ReasonCategory NVARCHAR(15) NOT NULL,        -- BREAKDOWN | SETUP_WAIT | MATERIAL_WAIT | QUALITY_HOLD | NO_OPERATOR | POWER | OTHER
            ReasonDetails NVARCHAR(300) NULL,
            StartedAt DATETIME2(7) NOT NULL,
            EndedAt DATETIME2(7) NULL,
            DowntimeMinutes AS (CASE WHEN EndedAt IS NOT NULL
                                     THEN DATEDIFF(MINUTE, StartedAt, EndedAt) END) PERSISTED,
            IsPlanned BIT NOT NULL DEFAULT 0,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_WorkOrderDowntimeLogs PRIMARY KEY CLUSTERED (DowntimeLogID),
            CONSTRAINT CK_WoDowntime_Category CHECK (ReasonCategory IN ('BREAKDOWN','SETUP_WAIT','MATERIAL_WAIT','QUALITY_HOLD','NO_OPERATOR','POWER','OTHER')),
            CONSTRAINT CK_WoDowntime_Time CHECK (EndedAt IS NULL OR EndedAt > StartedAt)
        ) WITH (DATA_COMPRESSION = PAGE);
        CREATE NONCLUSTERED INDEX IX_WoDowntime_Op
            ON dbo.WorkOrderDowntimeLogs (CompanyID, WoOperationID, ReasonCategory) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- COMPLETION
    -- ==========================================================================
    COMMIT TRANSACTION;
    PRINT N'═══════════════════════════════════════════════════════════════════════════';
    PRINT N'✅ Work Order Execution Extensions deployed successfully.';
    PRINT N'   - Operation tracking + labor logs + OEE downtime classification.';
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
