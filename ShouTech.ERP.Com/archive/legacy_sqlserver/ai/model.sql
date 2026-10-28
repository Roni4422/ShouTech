-- ========================================================================
-- FILE: dba/ai/model.sql
-- PROJECT: SHOUTECH ERP V10 - AI Models (Ultimate 10/10)
-- VERSION: 10.2.0
-- ========================================================================
-- TABLE: AIModels + seed
-- ========================================================================

USE [$(DbFileName)];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

PRINT N'═══════════════════════════════════════════════════════════════════════';
PRINT N'SHOUTECH ERP V10 – AI Models (Ultimate 10/10)';
PRINT N'═══════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AIModels')
    BEGIN
        CREATE TABLE dbo.AIModels (
            ModelID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ModelCode NVARCHAR(50) NOT NULL,
            ModelName NVARCHAR(200) NOT NULL,
            ModelType NVARCHAR(50) NOT NULL,
            ModelVersion NVARCHAR(20) NOT NULL,
            ModelFormat NVARCHAR(20) NULL,
            ModelPath NVARCHAR(500) NULL,
            Accuracy DECIMAL(5,4) NULL,
            Precision_ DECIMAL(5,4) NULL,
            Recall DECIMAL(5,4) NULL,
            F1Score DECIMAL(5,4) NULL,
            TrainedAt DATETIME2(7) NULL,
            TrainingSamples INT NULL,
            IsActive BIT NOT NULL DEFAULT 1,
            IsProduction BIT NOT NULL DEFAULT 0,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_AIModels PRIMARY KEY CLUSTERED (ModelID),
            CONSTRAINT UQ_AIModels_Code UNIQUE (CompanyID, ModelCode, ModelVersion),
            CONSTRAINT CK_AIModels_Type CHECK (ModelType IN ('SALES_FORECAST', 'INVENTORY_OPTIMIZATION', 'FRAUD_DETECTION', 'CUSTOMER_CHURN', 'DEMAND_PREDICTION')),
            CONSTRAINT CK_AIModels_Format CHECK (ModelFormat IN ('ONNX', 'PICKLE', 'TENSORFLOW', 'SKLEARN'))
        );
        PRINT N'✅ AIModels created';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_AIModels_Company_Active ON dbo.AIModels(CompanyID, IsActive) INCLUDE (ModelType, ModelName, Accuracy) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_AIModels_Type ON dbo.AIModels(ModelType, IsProduction) WHERE IsDeleted = 0;

    -- Seed example model
    IF NOT EXISTS (SELECT 1 FROM dbo.AIModels WHERE ModelCode = 'SALES_FORECAST_V1')
    BEGIN
        DECLARE @SysCompanyID UNIQUEIDENTIFIER = (SELECT TOP 1 CompanyID FROM MST.dbo.Companies WHERE CompanyCode = 'SYSTEM');
        IF @SysCompanyID IS NOT NULL
        BEGIN
            INSERT INTO dbo.AIModels (CompanyID, ModelCode, ModelName, ModelType, ModelVersion, ModelFormat, IsProduction, IsActive, CreatedBy)
            VALUES (@SysCompanyID, 'SALES_FORECAST_V1', N'تنبؤ المبيعات الإصدار الأول', 'SALES_FORECAST', '1.0', 'ONNX', 1, 1, '00000000-0000-0000-0000-000000000001');
            PRINT N'✅ Example AI model seeded.';
        END
    END

    COMMIT TRANSACTION;
    PRINT N'✅ AI Models module deployed.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    DECLARE @ErrorMessage NVARCHAR(4000) = ERROR_MESSAGE();
    RAISERROR(@ErrorMessage, 16, 1);
END CATCH
GO