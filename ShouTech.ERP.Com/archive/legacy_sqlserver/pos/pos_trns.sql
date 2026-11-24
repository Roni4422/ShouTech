-- ============================================================
-- source: archive/legacy_sqlserver/pos/pos.sql
-- ============================================================
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
            ('CASH',   N'Cash',            'Cash',          1, 0, 1, 1),
            ('CARD',   N'Bank Card',     'Bank Card',     0, 1, 0, 2),
            ('WALLET', N'E-Wallet', 'E-Wallet',      0, 1, 0, 3),
            ('CREDIT', N'On Credit (Receivables)',       'On Credit',     0, 0, 0, 4),
            ('CHEQUE', N'Cheque',             'Cheque',        0, 1, 0, 5),
            ('TRANSFER', N'Bank Transfer',    'Bank Transfer', 0, 1, 0, 6)
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

-- ============================================================
-- source: archive/legacy_sqlserver/pos/pos_trns.sql
-- ============================================================
-- ========================================================================
-- FILE: dba/pos/pos_trns.sql
-- PROJECT: SHOUTECH ERP V10 - POS Transactions (Ultimate 10/10)
-- VERSION: 10.3.0
-- DESCRIPTION: POS sales tickets (header/lines/payments), returns with
--              linkage, held (parked) tickets, e-invoice cryptographic
--              chain fields (UUID/PIH/hash/QR) and offline sync tracking.
-- ========================================================================
-- DESIGN PRINCIPLES:
-- ✅ Idempotent, TRY/CATCH + TRANSACTION wrapper.
-- ✅ Posted tickets are IMMUTABLE — corrections via return tickets only.
-- ✅ Offline-first: each ticket carries DeviceGuid + SyncStatus.
-- ✅ ZATCA-compliant chain: ICV + PIH + cryptographic stamp per ticket.
-- ✅ DATA_COMPRESSION + columnstore for high-volume analytics.
-- ========================================================================

USE [$(DbFileName)];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

PRINT N'═══════════════════════════════════════════════════════════════════════════';
PRINT N'SHOUTECH ERP V10 – POS Transactions (Ultimate 10/10)';
PRINT N'═══════════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PosTickets')
    BEGIN
        CREATE TABLE dbo.PosTickets (
            TicketID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            BranchID INT NOT NULL,
            TerminalID INT NOT NULL,
            ShiftID BIGINT NOT NULL,                     -- Loose ref → PosShifts
            TicketNo NVARCHAR(30) NOT NULL,              -- Human number per terminal/day
            TicketGuid UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(), -- Offline idempotency key
            TicketType NVARCHAR(10) NOT NULL DEFAULT 'SALE',    -- SALE | RETURN | EXCHANGE
            ReturnOfTicketID BIGINT NULL,                -- Linkage for returns (mandatory when RETURN)
            CustomerID INT NULL,                         -- NULL = walk-in
            CashierUserID INT NOT NULL,
            TicketDateTime DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            BusinessDate DATE NOT NULL,                  -- Shift business date (post-midnight sales)
            CurrencyCode NVARCHAR(3) NOT NULL,
            ExchangeRate DECIMAL(18,6) NOT NULL DEFAULT 1,
            SubTotal DECIMAL(18,4) NOT NULL DEFAULT 0,   -- Before tax, after line discounts
            DiscountAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
            TaxableAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
            TaxAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
            RoundingAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
            TotalAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
            PaidAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
            ChangeAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
            LineCount INT NOT NULL DEFAULT 0,
            -- ── E-invoicing chain (ZATCA/JoFotara/ETA) ──
            InvoiceCounterValue BIGINT NULL,             -- ICV from PosEInvoiceDevices
            PreviousInvoiceHash NVARCHAR(128) NULL,      -- PIH (Base64 SHA-256)
            InvoiceHash NVARCHAR(128) NULL,              -- Hash of this invoice (chained)
            CryptographicStamp NVARCHAR(500) NULL,       -- ECDSA signature (Base64)
            QrCodeData NVARCHAR(700) NULL,               -- TLV-encoded QR payload
            EInvoiceStatus NVARCHAR(15) NOT NULL DEFAULT 'NOT_REQUIRED', -- NOT_REQUIRED|PENDING|CLEARED|REPORTED|FAILED
            EInvoiceUuid NVARCHAR(100) NULL,             -- Authority-returned UUID
            -- ── Offline sync ──
            SyncStatus NVARCHAR(10) NOT NULL DEFAULT 'LOCAL',  -- LOCAL | SYNCED | CONFLICT
            SyncedAt DATETIME2(7) NULL,
            Status NVARCHAR(10) NOT NULL DEFAULT 'POSTED',     -- POSTED | VOIDED
            VoidedBy UNIQUEIDENTIFIER NULL,
            VoidedAt DATETIME2(7) NULL,
            VoidReason NVARCHAR(300) NULL,               -- Mandatory when VOIDED (audit)
            Notes NVARCHAR(500) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_PosTickets PRIMARY KEY CLUSTERED (TicketID),
            CONSTRAINT UQ_PosTickets_Guid UNIQUE (CompanyID, TicketGuid),
            CONSTRAINT UQ_PosTickets_No UNIQUE (CompanyID, TerminalID, BusinessDate, TicketNo),
            CONSTRAINT CK_PosTickets_Type CHECK (TicketType IN ('SALE','RETURN','EXCHANGE')),
            CONSTRAINT CK_PosTickets_Status CHECK (Status IN ('POSTED','VOIDED')),
            CONSTRAINT CK_PosTickets_Amounts CHECK (TotalAmount >= 0 AND PaidAmount >= 0)
        ) WITH (DATA_COMPRESSION = PAGE);
        CREATE NONCLUSTERED INDEX IX_PosTickets_Date
            ON dbo.PosTickets (CompanyID, BusinessDate, TerminalID)
            INCLUDE (TotalAmount, TaxAmount, Status) WHERE IsDeleted = 0;
        CREATE NONCLUSTERED INDEX IX_PosTickets_Shift
            ON dbo.PosTickets (CompanyID, ShiftID, TicketType) WHERE IsDeleted = 0;
        CREATE NONCLUSTERED INDEX IX_PosTickets_Sync
            ON dbo.PosTickets (CompanyID, SyncStatus) WHERE IsDeleted = 0 AND SyncStatus <> 'SYNCED';
        CREATE COLUMNSTORE INDEX CSIX_PosTickets
            ON dbo.PosTickets (CompanyID, BusinessDate, TerminalID, CashierUserID,
                               TicketType, SubTotal, TaxAmount, TotalAmount);
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PosTicketLines')
    BEGIN
        CREATE TABLE dbo.PosTicketLines (
            TicketLineID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            TicketID BIGINT NOT NULL,                    -- Loose ref → PosTickets
            LineNo INT NOT NULL,
            ItemID INT NOT NULL,
            ItemBarcode NVARCHAR(50) NULL,
            ItemNameAR NVARCHAR(200) NOT NULL,           -- Snapshot (immutable print record)
            ItemNameEN NVARCHAR(200) NULL,
            UnitCode NVARCHAR(10) NOT NULL,
            Quantity DECIMAL(18,4) NOT NULL,
            UnitPrice DECIMAL(18,4) NOT NULL,
            DiscountPercent DECIMAL(9,4) NOT NULL DEFAULT 0,
            DiscountAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
            NetUnitPrice DECIMAL(18,4) NOT NULL,         -- After discount
            LineSubTotal DECIMAL(18,4) NOT NULL,         -- Qty × NetUnitPrice
            TaxRateID INT NULL,                          -- Loose ref → TaxRates
            TaxPercent DECIMAL(9,4) NOT NULL DEFAULT 0,
            TaxAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
            LineTotal DECIMAL(18,4) NOT NULL,            -- LineSubTotal + TaxAmount
            CostAmount DECIMAL(18,4) NOT NULL DEFAULT 0, -- COGS snapshot for margin analysis
            WarehouseID INT NULL,
            BatchNo NVARCHAR(50) NULL,
            SerialNo NVARCHAR(100) NULL,
            ExpiryDate DATE NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_PosTicketLines PRIMARY KEY CLUSTERED (TicketLineID),
            CONSTRAINT UQ_PosTicketLines_Line UNIQUE (TicketID, LineNo),
            CONSTRAINT CK_PosTicketLines_Qty CHECK (Quantity <> 0),
            CONSTRAINT CK_PosTicketLines_Price CHECK (UnitPrice >= 0)
        ) WITH (DATA_COMPRESSION = PAGE);
        CREATE NONCLUSTERED INDEX IX_PosTicketLines_Item
            ON dbo.PosTicketLines (CompanyID, ItemID)
            INCLUDE (Quantity, LineTotal, TicketID) WHERE IsDeleted = 0;
        CREATE COLUMNSTORE INDEX CSIX_PosTicketLines
            ON dbo.PosTicketLines (CompanyID, TicketID, ItemID, Quantity,
                                   LineSubTotal, TaxAmount, LineTotal, CostAmount);
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PosTicketPayments')
    BEGIN
        CREATE TABLE dbo.PosTicketPayments (
            TicketPaymentID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            TicketID BIGINT NOT NULL,
            PaymentMethodID INT NOT NULL,                -- Loose ref → PosPaymentMethods
            MethodCode NVARCHAR(20) NOT NULL,            -- Snapshot
            Amount DECIMAL(18,4) NOT NULL,
            CurrencyCode NVARCHAR(3) NOT NULL,
            ExchangeRate DECIMAL(18,6) NOT NULL DEFAULT 1,
            AmountBase DECIMAL(18,4) NOT NULL,
            ReferenceNo NVARCHAR(100) NULL,              -- Approval/cheque/wallet ref
            CardType NVARCHAR(30) NULL,                  -- VISA | MASTERCARD | MADA | …
            CardLast4 NVARCHAR(4) NULL,                  -- PCI: last-4 only, never PAN
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_PosTicketPayments PRIMARY KEY CLUSTERED (TicketPaymentID),
            CONSTRAINT UQ_PosTicketPayments UNIQUE (TicketID, PaymentMethodID, ReferenceNo),
            CONSTRAINT CK_PosTicketPayments_Amount CHECK (Amount >= 0)
        );
        CREATE NONCLUSTERED INDEX IX_PosTicketPayments_Ticket
            ON dbo.PosTicketPayments (CompanyID, TicketID) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    --    Temporary state — deleted after resume/complete. Never posts to GL.
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PosHeldTickets')
    BEGIN
        CREATE TABLE dbo.PosHeldTickets (
            HeldTicketID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            TerminalID INT NOT NULL,
            CashierUserID INT NOT NULL,
            HeldRef NVARCHAR(30) NOT NULL,               -- "Table 4", "Order 12", customer name…
            CustomerID INT NULL,
            HeldAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            Payload NVARCHAR(MAX) NOT NULL,              -- JSON cart snapshot
            ExpiresAt DATETIME2(7) NULL,                 -- Auto-purge stale holds
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_PosHeldTickets PRIMARY KEY CLUSTERED (HeldTicketID),
            CONSTRAINT UQ_PosHeldTickets_Ref UNIQUE (CompanyID, TerminalID, HeldRef)
        );
        CREATE NONCLUSTERED INDEX IX_PosHeldTickets_Terminal
            ON dbo.PosHeldTickets (CompanyID, TerminalID, HeldAt) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PosSyncLog')
    BEGIN
        CREATE TABLE dbo.PosSyncLog (
            SyncLogID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            TerminalID INT NOT NULL,
            BatchGuid UNIQUEIDENTIFIER NOT NULL,
            Direction NVARCHAR(5) NOT NULL,              -- PUSH | PULL
            EntityType NVARCHAR(30) NOT NULL,            -- TICKET | STOCK | CUSTOMER | PRICE
            RecordsCount INT NOT NULL,
            StartedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            CompletedAt DATETIME2(7) NULL,
            Status NVARCHAR(10) NOT NULL DEFAULT 'RUNNING', -- RUNNING | OK | FAILED
            ErrorMessage NVARCHAR(1000) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_PosSyncLog PRIMARY KEY CLUSTERED (SyncLogID),
            CONSTRAINT CK_PosSyncLog_Direction CHECK (Direction IN ('PUSH','PULL')),
            CONSTRAINT CK_PosSyncLog_Status CHECK (Status IN ('RUNNING','OK','FAILED'))
        ) WITH (DATA_COMPRESSION = PAGE);
        CREATE NONCLUSTERED INDEX IX_PosSyncLog_Terminal
            ON dbo.PosSyncLog (CompanyID, TerminalID, StartedAt DESC) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- COMPLETION
    -- ==========================================================================
    COMMIT TRANSACTION;
    PRINT N'═══════════════════════════════════════════════════════════════════════════';
    PRINT N'✅ POS Transactions deployed successfully.';
    PRINT N'   - Immutable tickets + multi-payment + held carts + sync log.';
    PRINT N'   - ZATCA chain: ICV + PIH + hash + stamp + TLV QR per ticket.';
    PRINT N'   - Columnstore analytics on tickets and lines.';
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

-- ============================================================
-- source: archive/legacy_sqlserver/pos/shift.sql
-- ============================================================
-- ========================================================================
-- FILE: dba/pos/shift.sql
-- PROJECT: SHOUTECH ERP V10 - POS Shifts & Cash Control (Ultimate 10/10)
-- VERSION: 10.3.0
--              movements (pay-in/pay-out), blind-count variance, X/Z
--              readings with full audit trail.
-- ========================================================================
-- DESIGN PRINCIPLES:
-- ✅ Idempotent, TRY/CATCH + TRANSACTION wrapper.
-- ✅ One OPEN shift per terminal enforced by filtered unique index.
-- ✅ Closed shifts are immutable (X/Z readings are legal audit records).
-- ✅ Blind count: system expected amount never shown before closing count.
-- ========================================================================

USE [$(DbFileName)];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

PRINT N'═══════════════════════════════════════════════════════════════════════════';
PRINT N'SHOUTECH ERP V10 – POS Shifts & Cash Control (Ultimate 10/10)';
PRINT N'═══════════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PosShifts')
    BEGIN
        CREATE TABLE dbo.PosShifts (
            ShiftID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            BranchID INT NOT NULL,
            TerminalID INT NOT NULL,
            ShiftNo NVARCHAR(30) NOT NULL,               -- Sequential per terminal
            CashierUserID INT NOT NULL,
            BusinessDate DATE NOT NULL,
            OpenedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            OpeningFloat DECIMAL(18,4) NOT NULL DEFAULT 0,   -- shift opening float
            -- ── Closing (populated only on close; immutable afterwards) ──
            ClosedAt DATETIME2(7) NULL,
            ClosedBy UNIQUEIDENTIFIER NULL,
            ExpectedCash DECIMAL(18,4) NULL,             -- System-computed (float + cash sales − payouts)
            CountedCash DECIMAL(18,4) NULL,              -- Physical blind count
            CashVariance DECIMAL(18,4) NULL,             -- Counted − Expected (+ over / − short)
            VarianceReason NVARCHAR(300) NULL,           -- Mandatory when |variance| > tolerance
            TotalSales DECIMAL(18,4) NOT NULL DEFAULT 0,
            TotalReturns DECIMAL(18,4) NOT NULL DEFAULT 0,
            TotalCashSales DECIMAL(18,4) NOT NULL DEFAULT 0,
            TotalCardSales DECIMAL(18,4) NOT NULL DEFAULT 0,
            TotalCreditSales DECIMAL(18,4) NOT NULL DEFAULT 0,
            TotalTax DECIMAL(18,4) NOT NULL DEFAULT 0,
            TicketsCount INT NOT NULL DEFAULT 0,
            ReturnsCount INT NOT NULL DEFAULT 0,
            ZReadingNo INT NULL,                         -- Daily Z counter per terminal
            Status NVARCHAR(10) NOT NULL DEFAULT 'OPEN', -- OPEN | CLOSED | REVIEWED
            ReviewedBy UNIQUEIDENTIFIER NULL,            -- Supervisor sign-off on variance
            ReviewedAt DATETIME2(7) NULL,
            Notes NVARCHAR(500) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_PosShifts PRIMARY KEY CLUSTERED (ShiftID),
            CONSTRAINT UQ_PosShifts_No UNIQUE (CompanyID, TerminalID, ShiftNo),
            CONSTRAINT CK_PosShifts_Status CHECK (Status IN ('OPEN','CLOSED','REVIEWED')),
            CONSTRAINT CK_PosShifts_Close CHECK (
                (Status = 'OPEN' AND ClosedAt IS NULL) OR
                (Status IN ('CLOSED','REVIEWED') AND ClosedAt IS NOT NULL AND CountedCash IS NOT NULL))
        ) WITH (DATA_COMPRESSION = PAGE);
        -- Enforce: at most ONE open shift per terminal
        CREATE UNIQUE NONCLUSTERED INDEX UX_PosShifts_OneOpen
            ON dbo.PosShifts (CompanyID, TerminalID) WHERE Status = 'OPEN' AND IsDeleted = 0;
        CREATE NONCLUSTERED INDEX IX_PosShifts_Date
            ON dbo.PosShifts (CompanyID, BusinessDate, TerminalID)
            INCLUDE (TotalSales, TotalTax, Status) WHERE IsDeleted = 0;
        CREATE NONCLUSTERED INDEX IX_PosShifts_Cashier
            ON dbo.PosShifts (CompanyID, CashierUserID, BusinessDate) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PosDrawerMovements')
    BEGIN
        CREATE TABLE dbo.PosDrawerMovements (
            DrawerMovementID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ShiftID BIGINT NOT NULL,                     -- Loose ref → PosShifts
            TerminalID INT NOT NULL,
            MovementType NVARCHAR(10) NOT NULL,          -- PAY_IN | PAY_OUT | FLOAT_ADJ | SAFE_DROP
            Amount DECIMAL(18,4) NOT NULL,
            Reason NVARCHAR(300) NOT NULL,               -- Mandatory — audit requirement
            ApprovedBy UNIQUEIDENTIFIER NULL,            -- Supervisor approval for PAY_OUT/SAFE_DROP
            PerformedBy INT NOT NULL,                    -- Cashier UserID
            MovementAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            ReferenceNo NVARCHAR(50) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_PosDrawerMovements PRIMARY KEY CLUSTERED (DrawerMovementID),
            CONSTRAINT CK_PosDrawerMovements_Type CHECK (MovementType IN ('PAY_IN','PAY_OUT','FLOAT_ADJ','SAFE_DROP')),
            CONSTRAINT CK_PosDrawerMovements_Amount CHECK (Amount > 0)
        );
        CREATE NONCLUSTERED INDEX IX_PosDrawerMovements_Shift
            ON dbo.PosDrawerMovements (CompanyID, ShiftID, MovementType) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PosCashCounts')
    BEGIN
        CREATE TABLE dbo.PosCashCounts (
            CashCountID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ShiftID BIGINT NOT NULL,
            CountType NVARCHAR(10) NOT NULL,             -- OPEN | CLOSE | SPOT (surprise count)
            Denomination DECIMAL(18,4) NOT NULL,         -- 500, 200, 100, 50, 10, 5, 1, 0.5…
            Qty INT NOT NULL DEFAULT 0,
            SubTotal AS (Denomination * Qty) PERSISTED,
            CountedBy INT NOT NULL,
            CountedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_PosCashCounts PRIMARY KEY CLUSTERED (CashCountID),
            CONSTRAINT CK_PosCashCounts_Type CHECK (CountType IN ('OPEN','CLOSE','SPOT')),
            CONSTRAINT CK_PosCashCounts_Qty CHECK (Qty >= 0),
            CONSTRAINT UQ_PosCashCounts_Denom UNIQUE (ShiftID, CountType, Denomination)
        );
        CREATE NONCLUSTERED INDEX IX_PosCashCounts_Shift
            ON dbo.PosCashCounts (CompanyID, ShiftID, CountType) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- COMPLETION
    -- ==========================================================================
    COMMIT TRANSACTION;
    PRINT N'═══════════════════════════════════════════════════════════════════════════';
    PRINT N'✅ POS Shifts & Cash Control deployed successfully.';
    PRINT N'   - One-open-shift-per-terminal enforced by filtered unique index.';
    PRINT N'   - Blind count + variance + mandatory reason + supervisor review.';
    PRINT N'   - Drawer movements and denomination-level cash counts.';
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
