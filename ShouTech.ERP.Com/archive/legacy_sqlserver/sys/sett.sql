-- ═════════════════════════════════════════════════════════════════════════════════
-- FILE: dba/sys/sett.sql
-- SYSTEM: ShouTech ERP Enterprise Core
-- MODULE: Enterprise Settings & Configuration Matrix
-- GRADE: Enterprise Production Standard (Tier-1)
-- ═════════════════════════════════════════════════════════════════════════════════

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

RAISERROR(N'🚀 [ENTERPRISE BUILD] Building Configuration Engine (dba/sys/sett.sql)...', 0, 1) WITH NOWAIT;

BEGIN TRANSACTION;

BEGIN TRY

    IF OBJECT_ID(N'sys.SystemSettings', N'U') IS NULL
    BEGIN
        CREATE TABLE sys.SystemSettings (
            SettingID           INT IDENTITY(1,1) NOT NULL,
            TenantID            INT NOT NULL DEFAULT 1,
            CompanyID           INT NOT NULL DEFAULT 1,
            SettingKey          VARCHAR(100) NOT NULL,
            SettingValue        NVARCHAR(MAX) NULL,
            DataType            VARCHAR(20) NOT NULL DEFAULT 'STRING', -- STRING, INT, DECIMAL, BOOL, JSON
            Category            VARCHAR(50) NOT NULL DEFAULT 'GENERAL',-- SYSTEM, FINANCE, ZATCA, INVENTORY
            DescriptionAR       NVARCHAR(300) NULL,
            DescriptionEN       VARCHAR(300) NULL,
            IsEncrypted         BIT NOT NULL DEFAULT 0,
            IsSystemOnly        BIT NOT NULL DEFAULT 0,
            
            -- Audit
            UpdatedBy           INT NULL,
            UpdatedAt           DATETIME2(7) NOT NULL DEFAULT SYSDATETIME(),
            RowVersion          ROWVERSION NOT NULL,

            CONSTRAINT PK_sys_SystemSettings PRIMARY KEY CLUSTERED (SettingID),
            CONSTRAINT UQ_sys_SystemSettings_Key UNIQUE (TenantID, CompanyID, SettingKey)
        );
    END;

    MERGE INTO sys.SystemSettings AS Target
    USING (VALUES 
        (1, 1, 'COMPANY_NAME_AR', N'شركة شو تيك للحلول التقنية', 'STRING', 'GENERAL', N'اسم الشركة باللغة العربية'),
        (1, 1, 'COMPANY_NAME_EN', 'ShouTech Solutions Co.', 'STRING', 'GENERAL', N'اسم الشركة باللغة الإنجليزية'),
        (1, 1, 'VAT_NUMBER', '300000000000003', 'STRING', 'FINANCE', N'الرقم الضريبي للشركة'),
        (1, 1, 'VAT_DEFAULT_RATE', '15.00', 'DECIMAL', 'FINANCE', N'نسبة ضريبة القيمة المضافة الافتراضية'),
        (1, 1, 'BASE_CURRENCY_CODE', 'SAR', 'STRING', 'FINANCE', N'العملة الأساسية للنظام'),
        (1, 1, 'ENABLE_MULTI_CURRENCY', 'false', 'BOOL', 'FINANCE', N'تفعيل تعدد العملات'),
        (1, 1, 'ZATCA_ENVIRONMENT', 'DEVELOPMENT', 'STRING', 'ZATCA', N'بيئة ربط هيئة الزكاة والضريبة (SANDBOX, SIMULATION, PRODUCTION)')
    ) AS Source (TenantID, CompanyID, SettingKey, SettingValue, DataType, Category, DescriptionAR)
    ON (Target.TenantID = Source.TenantID AND Target.CompanyID = Source.CompanyID AND Target.SettingKey = Source.SettingKey)
    WHEN NOT MATCHED THEN
        INSERT (TenantID, CompanyID, SettingKey, SettingValue, DataType, Category, DescriptionAR)
        VALUES (Source.TenantID, Source.CompanyID, Source.SettingKey, Source.SettingValue, Source.DataType, Source.Category, Source.DescriptionAR);

    COMMIT TRANSACTION;
    RAISERROR(N'✅ [SUCCESS] Enterprise Settings table created and configured.', 0, 1) WITH NOWAIT;

END TRY
BEGIN CATCH
    IF (XACT_STATE()) <> 0 ROLLBACK TRANSACTION;
    DECLARE @Err NVARCHAR(4000) = ERROR_MESSAGE();
    RAISERROR(N'❌ [FATAL ERROR] sett.sql failed: %s', 16, 1, @Err);
END CATCH;
GO