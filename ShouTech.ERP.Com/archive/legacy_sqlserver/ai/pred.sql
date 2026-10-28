-- ========================================================================
-- FILE: dba/ai/pred.sql
-- PROJECT: SHOUTECH ERP V10 - AI Predictions (Ultimate 10/10)
-- VERSION: 10.2.0
-- ========================================================================
-- TABLE: AIPredictions
-- ========================================================================

USE [$(DbFileName)];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

PRINT N'═══════════════════════════════════════════════════════════════════════';
PRINT N'SHOUTECH ERP V10 – AI Predictions (Ultimate 10/10)';
PRINT N'═══════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AIPredictions')
    BEGIN
        CREATE TABLE dbo.AIPredictions (
            PredictionID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ModelID INT NOT NULL,
            PredictionType NVARCHAR(50) NOT NULL,
            PredictionDate DATE NOT NULL,
            TargetDate DATE NULL,
            InputData NVARCHAR(MAX) NULL,
            OutputData NVARCHAR(MAX) NULL,
            ConfidenceScore DECIMAL(5,4) NULL,
            EntityType NVARCHAR(50) NULL,
            EntityID INT NULL,
            ActualValue DECIMAL(18,4) NULL,
            PredictedValue DECIMAL(18,4) NULL,
            Variance AS (ABS(ActualValue - PredictedValue)) PERSISTED,
            IsReviewed BIT NOT NULL DEFAULT 0,
            ReviewedBy UNIQUEIDENTIFIER NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_AIPredictions PRIMARY KEY CLUSTERED (PredictionID)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ AIPredictions created';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_AIPredictions_Type_Date ON dbo.AIPredictions(PredictionType, PredictionDate DESC) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_AIPredictions_Model ON dbo.AIPredictions(ModelID, PredictionDate DESC) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_AIPredictions_Entity ON dbo.AIPredictions(EntityType, EntityID) WHERE EntityType IS NOT NULL AND IsDeleted = 0;
    CREATE NONCLUSTERED COLUMNSTORE INDEX IF NOT EXISTS CSIX_AIPredictions_Analytics ON dbo.AIPredictions (PredictionType, PredictionDate, ConfidenceScore, PredictedValue, ActualValue);

    COMMIT TRANSACTION;
    PRINT N'✅ AI Predictions module deployed.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    DECLARE @ErrorMessage NVARCHAR(4000) = ERROR_MESSAGE();
    RAISERROR(@ErrorMessage, 16, 1);
END CATCH
GO