-- ========================================================================
-- FILE: dba/mfg/qual.sql
-- PROJECT: SHOUTECH ERP V10 - Quality Management Extensions (Ultimate 10/10)
-- VERSION: 10.3.0
-- DESCRIPTION: Inspection plans (AQL sampling, ISO 2859), non-conformance
--              reports (NCR) and corrective/preventive actions (CAPA).
--              NOTE: dbo.QualityTests + dbo.QualityTestResults → dba/mfg/mfg.sql.
-- ========================================================================
-- ✅ Idempotent | TRY/CATCH + TRANSACTION | Soft Delete + RowVersion
-- ✅ ISO 9001 traceability: NCR → CAPA linkage is mandatory.
-- ========================================================================

USE [$(DbFileName)];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

PRINT N'═══════════════════════════════════════════════════════════════════════════';
PRINT N'SHOUTECH ERP V10 – Quality Management Extensions (Ultimate 10/10)';
PRINT N'═══════════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'QualityPlans')
    BEGIN
        CREATE TABLE dbo.QualityPlans (
            QualityPlanID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            PlanCode NVARCHAR(30) NOT NULL,
            PlanNameAR NVARCHAR(200) NOT NULL,
            PlanNameEN NVARCHAR(200) NULL,
            AppliesTo NVARCHAR(15) NOT NULL,             -- ITEM | BLUEPRINT | SUPPLIER | WORK_ORDER
            AppliesToID INT NOT NULL,
            InspectionTrigger NVARCHAR(15) NOT NULL,     -- RECEIVING | IN_PROCESS | FINAL | PRE_SHIPMENT
            SamplingStandard NVARCHAR(15) NOT NULL DEFAULT 'ISO2859', -- ISO2859 | FULL | FIXED_PCT | NONE
            AqlLevel NVARCHAR(10) NULL,                  -- e.g. 'II', 'S-3'
            AqlValue DECIMAL(5,2) NULL,                  -- e.g. 1.0, 2.5
            FixedSamplePct DECIMAL(5,2) NULL,            -- When FIXED_PCT
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_QualityPlans PRIMARY KEY CLUSTERED (QualityPlanID),
            CONSTRAINT UQ_QualityPlans_Code UNIQUE (CompanyID, PlanCode),
            CONSTRAINT CK_QualityPlans_AppliesTo CHECK (AppliesTo IN ('ITEM','BLUEPRINT','SUPPLIER','WORK_ORDER')),
            CONSTRAINT CK_QualityPlans_Trigger CHECK (InspectionTrigger IN ('RECEIVING','IN_PROCESS','FINAL','PRE_SHIPMENT')),
            CONSTRAINT CK_QualityPlans_Sampling CHECK (SamplingStandard IN ('ISO2859','FULL','FIXED_PCT','NONE'))
        );
        CREATE NONCLUSTERED INDEX IX_QualityPlans_Target
            ON dbo.QualityPlans (CompanyID, AppliesTo, AppliesToID) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'NonConformanceReports')
    BEGIN
        CREATE TABLE dbo.NonConformanceReports (
            NcrID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            NcrNo NVARCHAR(30) NOT NULL,                 -- NCR-2026-0001
            SourceType NVARCHAR(15) NOT NULL,            -- RECEIVING | PRODUCTION | CUSTOMER_RETURN | AUDIT | SUPPLIER
            SourceDocumentID BIGINT NULL,                -- GRN / WorkOrder / Sales return
            ItemID INT NOT NULL,
            BatchNo NVARCHAR(50) NULL,
            QtyAffected DECIMAL(18,4) NOT NULL,
            Severity NVARCHAR(10) NOT NULL DEFAULT 'MAJOR', -- CRITICAL | MAJOR | MINOR
            DefectDescriptionAR NVARCHAR(1000) NOT NULL,
            DefectDescriptionEN NVARCHAR(1000) NULL,
            DetectedBy INT NOT NULL,                     -- EmployeeID
            DetectedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            Disposition NVARCHAR(15) NULL,               -- USE_AS_IS | REWORK | SCRAP | RETURN_SUPPLIER | SORT
            DispositionBy UNIQUEIDENTIFIER NULL,
            DispositionAt DATETIME2(7) NULL,
            EstimatedCost DECIMAL(18,4) NOT NULL DEFAULT 0,
            Status NVARCHAR(12) NOT NULL DEFAULT 'OPEN', -- OPEN | DISPOSITIONED | CLOSED
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_NonConformanceReports PRIMARY KEY CLUSTERED (NcrID),
            CONSTRAINT UQ_NonConformanceReports_No UNIQUE (CompanyID, NcrNo),
            CONSTRAINT CK_NCR_Source CHECK (SourceType IN ('RECEIVING','PRODUCTION','CUSTOMER_RETURN','AUDIT','SUPPLIER')),
            CONSTRAINT CK_NCR_Severity CHECK (Severity IN ('CRITICAL','MAJOR','MINOR')),
            CONSTRAINT CK_NCR_Disposition CHECK (Disposition IS NULL OR Disposition IN ('USE_AS_IS','REWORK','SCRAP','RETURN_SUPPLIER','SORT')),
            CONSTRAINT CK_NCR_Status CHECK (Status IN ('OPEN','DISPOSITIONED','CLOSED'))
        ) WITH (DATA_COMPRESSION = PAGE);
        CREATE NONCLUSTERED INDEX IX_NCR_Item
            ON dbo.NonConformanceReports (CompanyID, ItemID, DetectedAt DESC) WHERE IsDeleted = 0;
        CREATE NONCLUSTERED INDEX IX_NCR_Open
            ON dbo.NonConformanceReports (CompanyID, Status, Severity) WHERE IsDeleted = 0 AND Status <> 'CLOSED';
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CorrectiveActions')
    BEGIN
        CREATE TABLE dbo.CorrectiveActions (
            CapaID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            CapaNo NVARCHAR(30) NOT NULL,                -- CAPA-2026-0001
            ActionType NVARCHAR(10) NOT NULL,            -- CORRECTIVE | PREVENTIVE
            NcrID BIGINT NULL,                           -- Mandatory when CORRECTIVE (ISO 9001)
            RootCause NVARCHAR(1000) NULL,               -- 5-Why / fishbone summary
            ActionPlanAR NVARCHAR(2000) NOT NULL,
            ActionPlanEN NVARCHAR(2000) NULL,
            ResponsibleEmployeeID INT NOT NULL,
            TargetDate DATE NOT NULL,
            CompletedDate DATE NULL,
            EffectivenessChecked BIT NOT NULL DEFAULT 0,
            EffectivenessNotes NVARCHAR(1000) NULL,
            Status NVARCHAR(12) NOT NULL DEFAULT 'OPEN', -- OPEN | IN_PROGRESS | COMPLETED | VERIFIED | CANCELLED
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_CorrectiveActions PRIMARY KEY CLUSTERED (CapaID),
            CONSTRAINT UQ_CorrectiveActions_No UNIQUE (CompanyID, CapaNo),
            CONSTRAINT CK_CAPA_Type CHECK (ActionType IN ('CORRECTIVE','PREVENTIVE')),
            CONSTRAINT CK_CAPA_Status CHECK (Status IN ('OPEN','IN_PROGRESS','COMPLETED','VERIFIED','CANCELLED')),
            -- ISO 9001: a CORRECTIVE action must trace back to an NCR
            CONSTRAINT CK_CAPA_NcrRequired CHECK (ActionType <> 'CORRECTIVE' OR NcrID IS NOT NULL)
        ) WITH (DATA_COMPRESSION = PAGE);
        CREATE NONCLUSTERED INDEX IX_CAPA_Open
            ON dbo.CorrectiveActions (CompanyID, Status, TargetDate) WHERE IsDeleted = 0 AND Status NOT IN ('VERIFIED','CANCELLED');
    END

    -- ==========================================================================
    -- COMPLETION
    -- ==========================================================================
    COMMIT TRANSACTION;
    PRINT N'═══════════════════════════════════════════════════════════════════════════';
    PRINT N'✅ Quality Management Extensions deployed successfully.';
    PRINT N'   - AQL plans (ISO 2859) + NCR + CAPA with ISO 9001 mandatory linkage.';
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
