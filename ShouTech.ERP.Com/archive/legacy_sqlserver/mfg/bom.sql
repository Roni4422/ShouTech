-- ========================================================================
-- FILE: dba/mfg/bom.sql
-- PROJECT: SHOUTECH ERP V10 - BOM Governance Extensions (Ultimate 10/10)
-- VERSION: 10.3.0
-- DESCRIPTION: Engineering Change Notices (ECN/ECO) with approval workflow
--              and frozen BOM cost rollups for variance analysis.
--              NOTE: dbo.ProductionBlueprints + inputs/outputs → dba/mfg/mfg.sql.
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
PRINT N'SHOUTECH ERP V10 – BOM Governance Extensions (Ultimate 10/10)';
PRINT N'═══════════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- ==========================================================================
    --    No BOM change without an approved ECN — full traceability (ISO 9001).
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'EngineeringChangeNotices')
    BEGIN
        CREATE TABLE dbo.EngineeringChangeNotices (
            EcnID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            EcnNo NVARCHAR(30) NOT NULL,                 -- ECN-2026-0001
            BlueprintID INT NOT NULL,                    -- Loose ref → ProductionBlueprints
            ChangeType NVARCHAR(15) NOT NULL,            -- ADD_ITEM | REMOVE_ITEM | QTY_CHANGE | SUBSTITUTE | ROUTE_CHANGE
            Priority NVARCHAR(10) NOT NULL DEFAULT 'NORMAL', -- LOW | NORMAL | HIGH | EMERGENCY
            ReasonCode NVARCHAR(20) NOT NULL,            -- COST_REDUCTION | QUALITY | SUPPLIER | CUSTOMER_REQ | REGULATORY
            DescriptionAR NVARCHAR(1000) NOT NULL,
            DescriptionEN NVARCHAR(1000) NULL,
            AffectedItemID INT NULL,
            OldValue NVARCHAR(200) NULL,
            NewValue NVARCHAR(200) NULL,
            EffectiveDate DATE NOT NULL,
            Status NVARCHAR(12) NOT NULL DEFAULT 'DRAFT', -- DRAFT | SUBMITTED | APPROVED | REJECTED | IMPLEMENTED | CANCELLED
            RequestedBy INT NOT NULL,                    -- EmployeeID
            RequestedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            ApprovedBy UNIQUEIDENTIFIER NULL,
            ApprovedAt DATETIME2(7) NULL,
            ImplementedAt DATETIME2(7) NULL,
            RejectionReason NVARCHAR(300) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_EngineeringChangeNotices PRIMARY KEY CLUSTERED (EcnID),
            CONSTRAINT UQ_EngineeringChangeNotices_No UNIQUE (CompanyID, EcnNo),
            CONSTRAINT CK_ECN_Status CHECK (Status IN ('DRAFT','SUBMITTED','APPROVED','REJECTED','IMPLEMENTED','CANCELLED')),
            CONSTRAINT CK_ECN_Priority CHECK (Priority IN ('LOW','NORMAL','HIGH','EMERGENCY'))
        ) WITH (DATA_COMPRESSION = PAGE);
        CREATE NONCLUSTERED INDEX IX_ECN_Blueprint
            ON dbo.EngineeringChangeNotices (CompanyID, BlueprintID, Status) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    --    Frozen snapshots → variance analysis against WorkOrder actuals.
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'BomCostRollups')
    BEGIN
        CREATE TABLE dbo.BomCostRollups (
            BomCostRollupID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            BlueprintID INT NOT NULL,
            RollupDate DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            CostingMethod NVARCHAR(15) NOT NULL,         -- STANDARD | AVERAGE | LAST | FIFO
            MaterialCost DECIMAL(18,4) NOT NULL DEFAULT 0,
            LaborCost DECIMAL(18,4) NOT NULL DEFAULT 0,
            OverheadCost DECIMAL(18,4) NOT NULL DEFAULT 0,
            SubcontractCost DECIMAL(18,4) NOT NULL DEFAULT 0,
            AdditionalCosts DECIMAL(18,4) NOT NULL DEFAULT 0,
            TotalCost AS (MaterialCost + LaborCost + OverheadCost + SubcontractCost + AdditionalCosts) PERSISTED,
            OutputQty DECIMAL(18,4) NOT NULL DEFAULT 1,
            UnitCost AS (CASE WHEN OutputQty > 0
                              THEN (MaterialCost + LaborCost + OverheadCost + SubcontractCost + AdditionalCosts) / OutputQty
                              ELSE 0 END) PERSISTED,
            CurrencyCode NVARCHAR(3) NOT NULL,
            Notes NVARCHAR(500) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_BomCostRollups PRIMARY KEY CLUSTERED (BomCostRollupID),
            CONSTRAINT CK_BomCostRollups_Method CHECK (CostingMethod IN ('STANDARD','AVERAGE','LAST','FIFO'))
        ) WITH (DATA_COMPRESSION = PAGE);
        CREATE NONCLUSTERED INDEX IX_BomCostRollups_Blueprint
            ON dbo.BomCostRollups (CompanyID, BlueprintID, RollupDate DESC) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- COMPLETION
    -- ==========================================================================
    COMMIT TRANSACTION;
    PRINT N'═══════════════════════════════════════════════════════════════════════════';
    PRINT N'✅ BOM Governance Extensions deployed successfully.';
    PRINT N'   - ECN approval workflow (ISO 9001) + frozen cost rollups.';
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
