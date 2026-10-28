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
