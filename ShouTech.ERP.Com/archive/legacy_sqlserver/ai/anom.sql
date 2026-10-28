-- ========================================================================
-- FILE: dba/ai/anom.sql
-- PROJECT: SHOUTECH ERP V10 - AI Anomalies (Ultimate 10/10)
-- VERSION: 10.2.0
-- ========================================================================
-- TABLE: AIAnomalies
-- ========================================================================

USE [$(DbFileName)];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

PRINT N'═══════════════════════════════════════════════════════════════════════';
PRINT N'SHOUTECH ERP V10 – AI Anomalies (Ultimate 10/10)';
PRINT N'═══════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AIAnomalies')
    BEGIN
        CREATE TABLE dbo.AIAnomalies (
            AnomalyID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            AnomalyType NVARCHAR(50) NOT NULL,
            Severity NVARCHAR(10) NOT NULL,
            EntityType NVARCHAR(50) NOT NULL,
            EntityID BIGINT NOT NULL,
            Description NVARCHAR(MAX) NOT NULL,
            Details NVARCHAR(MAX) NULL,
            ConfidenceScore DECIMAL(5,4) NULL,
            DetectedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            AnomalyStatus NVARCHAR(20) NOT NULL DEFAULT 'OPEN',
            ResolvedBy UNIQUEIDENTIFIER NULL,
            ResolvedAt DATETIME2(7) NULL,
            Resolution NVARCHAR(MAX) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_AIAnomalies PRIMARY KEY CLUSTERED (AnomalyID),
            CONSTRAINT CK_Anomaly_Type CHECK (AnomalyType IN ('FRAUD', 'UNUSUAL_TRANSACTION', 'PRICE_ANOMALY', 'STOCK_ANOMALY', 'QUALITY_ISSUE')),
            CONSTRAINT CK_Anomaly_Severity CHECK (Severity IN ('LOW', 'MEDIUM', 'HIGH', 'CRITICAL')),
            CONSTRAINT CK_Anomaly_Status CHECK (AnomalyStatus IN ('OPEN', 'INVESTIGATING', 'RESOLVED', 'FALSE_POSITIVE'))
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ AIAnomalies created';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_AIAnomalies_Open ON dbo.AIAnomalies(CompanyID, AnomalyStatus, Severity) WHERE AnomalyStatus IN ('OPEN', 'INVESTIGATING') AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_AIAnomalies_Entity ON dbo.AIAnomalies(EntityType, EntityID) WHERE IsDeleted = 0;

    COMMIT TRANSACTION;
    PRINT N'✅ AI Anomalies module deployed.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    DECLARE @ErrorMessage NVARCHAR(4000) = ERROR_MESSAGE();
    RAISERROR(@ErrorMessage, 16, 1);
END CATCH
GO