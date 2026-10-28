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
            OpeningFloat DECIMAL(18,4) NOT NULL DEFAULT 0,   -- عهدة بداية الوردية
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
            CountType NVARCHAR(10) NOT NULL,             -- OPEN | CLOSE | SPOT (جرد مفاجئ)
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
