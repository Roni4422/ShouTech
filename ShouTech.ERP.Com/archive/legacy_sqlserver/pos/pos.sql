-- ========================================================================
-- FILE: dba/pos/pos.sql
-- PROJECT: SHOUTECH ERP V10 - POS Core Configuration (Ultimate 10/10)
-- VERSION: 10.3.0
-- DESCRIPTION: POS terminals, device profiles (printer/scanner/drawer/
--              customer display), payment methods, receipt templates and
--              ZATCA Phase-2 device registration data (CSR/certificate).
-- ========================================================================
-- DESIGN PRINCIPLES:
-- ✅ Idempotent, TRY/CATCH + TRANSACTION wrapper.
-- ✅ Soft Delete + RowVersion + unified audit columns on ALL tables.
-- ✅ Secrets NEVER stored in plaintext (DPAPI-encrypted blobs only).
-- ✅ No direct FKs (deferred to post.sql).
-- ========================================================================

USE [$(DbFileName)];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

PRINT N'═══════════════════════════════════════════════════════════════════════════';
PRINT N'SHOUTECH ERP V10 – POS Core Configuration (Ultimate 10/10)';
PRINT N'═══════════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PosTerminals')
    BEGIN
        CREATE TABLE dbo.PosTerminals (
            TerminalID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            BranchID INT NOT NULL,                       -- Loose ref → Branches
            WarehouseID INT NULL,                        -- Default fulfillment warehouse
            TerminalCode NVARCHAR(20) NOT NULL,
            TerminalNameAR NVARCHAR(150) NOT NULL,
            TerminalNameEN NVARCHAR(150) NULL,
            MachineName NVARCHAR(100) NULL,              -- Windows host name binding
            HardwareFingerprint NVARCHAR(256) NULL,      -- HWID (anti-clone, SecEng)
            DefaultPriceListID INT NULL,
            DefaultCustomerID INT NULL,                  -- Walk-in customer
            AllowNegativeStock BIT NOT NULL DEFAULT 0,
            RequireShiftOpen BIT NOT NULL DEFAULT 1,
            OfflineModeEnabled BIT NOT NULL DEFAULT 1,
            MaxOfflineHours INT NOT NULL DEFAULT 72,     -- Force sync after N hours
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_PosTerminals PRIMARY KEY CLUSTERED (TerminalID),
            CONSTRAINT UQ_PosTerminals_Code UNIQUE (CompanyID, BranchID, TerminalCode)
        );
        CREATE NONCLUSTERED INDEX IX_PosTerminals_Branch
            ON dbo.PosTerminals (CompanyID, BranchID, IsActive) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PosDeviceProfiles')
    BEGIN
        CREATE TABLE dbo.PosDeviceProfiles (
            DeviceProfileID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            TerminalID INT NOT NULL,                     -- Loose ref → PosTerminals
            DeviceType NVARCHAR(20) NOT NULL,            -- PRINTER | SCANNER | DRAWER | DISPLAY | SCALE | EFT
            DeviceModel NVARCHAR(100) NULL,
            ConnectionType NVARCHAR(15) NOT NULL,        -- USB | SERIAL | ETHERNET | BLUETOOTH | OPOS
            ConnectionString NVARCHAR(300) NULL,         -- Port/IP/baud — never credentials
            EncryptedCredential VARBINARY(MAX) NULL,     -- DPAPI blob only (e.g. EFT merchant key)
            PaperWidthMM TINYINT NULL,                   -- 58/80 for receipt printers
            OpenDrawerOnCash BIT NOT NULL DEFAULT 1,
            PrintLogo BIT NOT NULL DEFAULT 1,
            CopiesCount TINYINT NOT NULL DEFAULT 1,
            IsDefault BIT NOT NULL DEFAULT 0,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_PosDeviceProfiles PRIMARY KEY CLUSTERED (DeviceProfileID),
            CONSTRAINT CK_PosDeviceProfiles_Type CHECK (DeviceType IN ('PRINTER','SCANNER','DRAWER','DISPLAY','SCALE','EFT')),
            CONSTRAINT CK_PosDeviceProfiles_Conn CHECK (ConnectionType IN ('USB','SERIAL','ETHERNET','BLUETOOTH','OPOS'))
        );
        CREATE NONCLUSTERED INDEX IX_PosDeviceProfiles_Terminal
            ON dbo.PosDeviceProfiles (CompanyID, TerminalID, DeviceType) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PosPaymentMethods')
    BEGIN
        CREATE TABLE dbo.PosPaymentMethods (
            PaymentMethodID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            MethodCode NVARCHAR(20) NOT NULL,            -- CASH | CARD | WALLET | CREDIT | CHEQUE | BANK_TRANSFER
            MethodNameAR NVARCHAR(100) NOT NULL,
            MethodNameEN NVARCHAR(100) NOT NULL,
            GLAccountID INT NULL,                        -- Clearing account (loose ref → COA)
            OpensCashDrawer BIT NOT NULL DEFAULT 0,
            RequiresReference BIT NOT NULL DEFAULT 0,    -- Card approval no. / cheque no.
            AllowOverpayment BIT NOT NULL DEFAULT 0,     -- Give change
            MaxAmount DECIMAL(18,4) NULL,
            SurchargePercent DECIMAL(9,4) NOT NULL DEFAULT 0,
            RoundingRule NVARCHAR(10) NOT NULL DEFAULT 'NONE', -- NONE | NEAREST_05 | NEAREST_10
            SortOrder INT NOT NULL DEFAULT 0,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_PosPaymentMethods PRIMARY KEY CLUSTERED (PaymentMethodID),
            CONSTRAINT UQ_PosPaymentMethods_Code UNIQUE (CompanyID, MethodCode),
            CONSTRAINT CK_PosPaymentMethods_Rounding CHECK (RoundingRule IN ('NONE','NEAREST_05','NEAREST_10'))
        );
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PosReceiptTemplates')
    BEGIN
        CREATE TABLE dbo.PosReceiptTemplates (
            ReceiptTemplateID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            TemplateCode NVARCHAR(30) NOT NULL,
            TemplateNameAR NVARCHAR(150) NOT NULL,
            TemplateNameEN NVARCHAR(150) NULL,
            LanguageCode NVARCHAR(5) NOT NULL DEFAULT 'ar', -- ar | en | fr | tr
            HeaderLines NVARCHAR(1000) NULL,             -- Free header text
            FooterLines NVARCHAR(1000) NULL,             -- Thanks/return policy text
            ShowTaxBreakdown BIT NOT NULL DEFAULT 1,
            ShowCashierName BIT NOT NULL DEFAULT 1,
            ShowCustomerInfo BIT NOT NULL DEFAULT 0,
            ShowQrCode BIT NOT NULL DEFAULT 1,           -- ZATCA TLV QR mandatory in KSA
            ShowBarcode BIT NOT NULL DEFAULT 0,
            LayoutJson NVARCHAR(MAX) NULL,               -- Full layout definition (designer)
            IsDefault BIT NOT NULL DEFAULT 0,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_PosReceiptTemplates PRIMARY KEY CLUSTERED (ReceiptTemplateID),
            CONSTRAINT UQ_PosReceiptTemplates_Code UNIQUE (CompanyID, TemplateCode),
            CONSTRAINT CK_PosReceiptTemplates_Lang CHECK (LanguageCode IN ('ar','en','fr','tr'))
        );
    END

    -- ==========================================================================
    --    CSR + compliance certificate per terminal. Secrets encrypted only.
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PosEInvoiceDevices')
    BEGIN
        CREATE TABLE dbo.PosEInvoiceDevices (
            EInvoiceDeviceID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            TerminalID INT NOT NULL,
            Platform NVARCHAR(30) NOT NULL,              -- FATOORA | JOFOTARA | ETA | E-FATURA
            DeviceSerial NVARCHAR(100) NOT NULL,         -- Solution/device serial (UUID format)
            Environment NVARCHAR(10) NOT NULL DEFAULT 'PROD', -- SIM | PROD
            Otp NVARCHAR(10) NULL,                       -- One-time onboarding OTP (transient)
            CsrPem NVARCHAR(MAX) NULL,
            ComplianceCertificatePem NVARCHAR(MAX) NULL,
            EncryptedSecret VARBINARY(MAX) NULL,         -- DPAPI blob — binary secret
            CertificateSerial NVARCHAR(100) NULL,
            CertificateExpiry DATETIME2(7) NULL,
            OnboardedAt DATETIME2(7) NULL,
            LastInvoiceHash NVARCHAR(128) NULL,          -- Chain hash (PIH) of last e-invoice
            InvoiceCounter BIGINT NOT NULL DEFAULT 0,    -- ICV — strictly monotonic per device
            Status NVARCHAR(15) NOT NULL DEFAULT 'PENDING', -- PENDING | COMPLIANT | REPORTING | REVOKED
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_PosEInvoiceDevices PRIMARY KEY CLUSTERED (EInvoiceDeviceID),
            CONSTRAINT UQ_PosEInvoiceDevices_Serial UNIQUE (CompanyID, Platform, DeviceSerial),
            CONSTRAINT CK_PosEInvoiceDevices_Status CHECK (Status IN ('PENDING','COMPLIANT','REPORTING','REVOKED'))
        );
        CREATE NONCLUSTERED INDEX IX_PosEInvoiceDevices_Terminal
            ON dbo.PosEInvoiceDevices (CompanyID, TerminalID, Platform) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- 6. SEED DATA — default payment methods
    -- ==========================================================================
    PRINT N'✅ Seeding default POS payment methods...';

    IF NOT EXISTS (SELECT 1 FROM dbo.PosPaymentMethods WHERE MethodCode = 'CASH')
    BEGIN
        INSERT INTO dbo.PosPaymentMethods
            (CompanyID, MethodCode, MethodNameAR, MethodNameEN, OpensCashDrawer,
             RequiresReference, AllowOverpayment, SortOrder, CreatedBy)
        SELECT TOP 1 C.CompanyID, X.Code, X.NameAR, X.NameEN, X.Drawer, X.NeedsRef, X.Overpay, X.SortOrd,
               '00000000-0000-0000-0000-000000000001'
        FROM MST.dbo.Companies C
        CROSS JOIN (VALUES
            ('CASH',   N'نقدي',            'Cash',          1, 0, 1, 1),
            ('CARD',   N'بطاقة بنكية',     'Bank Card',     0, 1, 0, 2),
            ('WALLET', N'محفظة إلكترونية', 'E-Wallet',      0, 1, 0, 3),
            ('CREDIT', N'آجل (ذمم)',       'On Credit',     0, 0, 0, 4),
            ('CHEQUE', N'شيك',             'Cheque',        0, 1, 0, 5),
            ('TRANSFER', N'تحويل بنكي',    'Bank Transfer', 0, 1, 0, 6)
        ) X (Code, NameAR, NameEN, Drawer, NeedsRef, Overpay, SortOrd);
    END

    -- ==========================================================================
    -- COMPLETION
    -- ==========================================================================
    COMMIT TRANSACTION;
    PRINT N'═══════════════════════════════════════════════════════════════════════════';
    PRINT N'✅ POS Core Configuration deployed successfully.';
    PRINT N'   - Terminals + device profiles + payment methods + receipt templates.';
    PRINT N'   - ZATCA Phase-2 device onboarding (CSR/cert/PIH chain, ICV counter).';
    PRINT N'   - Secrets stored as DPAPI-encrypted blobs only.';
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
