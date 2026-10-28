-- ========================================================================
-- FILE: dba/hr/leave.sql
-- PROJECT: SHOUTECH ERP V10 - Leave Management Extensions (Ultimate 10/10)
-- VERSION: 10.3.0
-- DESCRIPTION: Leave types per jurisdiction (KSA Labor Law Art. 109/113,
--              UAE, Jordan, Egypt…), annual balances with accruals and
--              carry-forward rules.
--              NOTE: dbo.Leaves (requests) lives in dba/hr/hr.sql.
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
PRINT N'SHOUTECH ERP V10 – Leave Management Extensions (Ultimate 10/10)';
PRINT N'═══════════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LeaveTypes')
    BEGIN
        CREATE TABLE dbo.LeaveTypes (
            LeaveTypeID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            TypeCode NVARCHAR(20) NOT NULL,              -- ANNUAL | SICK | HAJJ | MATERNITY | PATERNITY | UNPAID | BEREAVEMENT | STUDY
            TypeNameAR NVARCHAR(150) NOT NULL,
            TypeNameEN NVARCHAR(150) NOT NULL,
            CountryCode NVARCHAR(2) NULL,                -- Jurisdiction-specific rules
            DefaultDaysPerYear DECIMAL(5,1) NOT NULL DEFAULT 0,
            IsPaid BIT NOT NULL DEFAULT 1,
            PaidPercentage DECIMAL(5,2) NOT NULL DEFAULT 100, -- Sick: 100% then 75%… (KSA Art.117)
            RequiresAttachment BIT NOT NULL DEFAULT 0,   -- Medical report etc.
            MaxConsecutiveDays DECIMAL(5,1) NULL,
            MinServiceMonths INT NOT NULL DEFAULT 0,     -- Eligibility (e.g. Hajj after 12m)
            Gender NVARCHAR(6) NULL,                     -- M | F | NULL both
            CountsWeekends BIT NOT NULL DEFAULT 0,
            AllowCarryForward BIT NOT NULL DEFAULT 0,
            MaxCarryForwardDays DECIMAL(5,1) NULL,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_LeaveTypes PRIMARY KEY CLUSTERED (LeaveTypeID),
            CONSTRAINT UQ_LeaveTypes_Code UNIQUE (CompanyID, TypeCode),
            CONSTRAINT CK_LeaveTypes_PaidPct CHECK (PaidPercentage BETWEEN 0 AND 100)
        );
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LeaveBalances')
    BEGIN
        CREATE TABLE dbo.LeaveBalances (
            LeaveBalanceID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            EmployeeID INT NOT NULL,
            LeaveTypeID INT NOT NULL,
            Year INT NOT NULL,
            OpeningBalance DECIMAL(5,1) NOT NULL DEFAULT 0,  -- Carried forward
            AccruedDays DECIMAL(5,1) NOT NULL DEFAULT 0,     -- Earned this year
            UsedDays DECIMAL(5,1) NOT NULL DEFAULT 0,
            PendingDays DECIMAL(5,1) NOT NULL DEFAULT 0,     -- In approval workflow
            AdjustedDays DECIMAL(5,1) NOT NULL DEFAULT 0,    -- Manual HR adjustments (audited)
            AdjustmentReason NVARCHAR(300) NULL,
            AvailableDays AS (OpeningBalance + AccruedDays - UsedDays - PendingDays + AdjustedDays) PERSISTED,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_LeaveBalances PRIMARY KEY CLUSTERED (LeaveBalanceID),
            CONSTRAINT UQ_LeaveBalances UNIQUE (CompanyID, EmployeeID, LeaveTypeID, Year)
        );
        CREATE NONCLUSTERED INDEX IX_LeaveBalances_Emp
            ON dbo.LeaveBalances (CompanyID, EmployeeID, Year) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LeaveAccruals')
    BEGIN
        CREATE TABLE dbo.LeaveAccruals (
            AccrualID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            EmployeeID INT NOT NULL,
            LeaveTypeID INT NOT NULL,
            AccrualDate DATE NOT NULL,
            PeriodYear INT NOT NULL,
            PeriodMonth TINYINT NOT NULL,
            AccruedDays DECIMAL(5,2) NOT NULL,
            AccrualBasis NVARCHAR(200) NULL,             -- e.g. '21 days / 12 months = 1.75'
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_LeaveAccruals PRIMARY KEY CLUSTERED (AccrualID),
            CONSTRAINT UQ_LeaveAccruals UNIQUE (CompanyID, EmployeeID, LeaveTypeID, PeriodYear, PeriodMonth),
            CONSTRAINT CK_LeaveAccruals_Month CHECK (PeriodMonth BETWEEN 1 AND 12)
        ) WITH (DATA_COMPRESSION = PAGE);
    END

    -- ==========================================================================
    -- 4. SEED DATA — Gulf & regional leave types (KSA Labor Law defaults)
    -- ==========================================================================
    PRINT N'✅ Seeding jurisdiction leave types...';

    IF NOT EXISTS (SELECT 1 FROM dbo.LeaveTypes WHERE TypeCode = 'ANNUAL')
    BEGIN
        INSERT INTO dbo.LeaveTypes
            (CompanyID, TypeCode, TypeNameAR, TypeNameEN, CountryCode, DefaultDaysPerYear,
             IsPaid, PaidPercentage, RequiresAttachment, MinServiceMonths, Gender, CreatedBy)
        SELECT TOP 1 C.CompanyID, X.Code, X.NameAR, X.NameEN, X.CC, X.Days,
               X.Paid, X.PaidPct, X.NeedsAtt, X.MinSvc, X.Gender, '00000000-0000-0000-0000-000000000001'
        FROM MST.dbo.Companies C
        CROSS JOIN (VALUES
            -- KSA Labor Law: Art.109 (21d → 30d after 5y), 113 (Hajj 10-15d once),
            --              114 (maternity 10w), 117 (sick: 30d full + 60d 75% + 30d unpaid)
            ('ANNUAL',    N'إجازة سنوية',            'Annual Leave',       'SA', 21.0,  1, 100.00, 0, 6,  NULL),
            ('ANNUAL-5Y', N'إجازة سنوية (بعد 5 سنوات)','Annual Leave (5+ years)','SA', 30.0, 1, 100.00, 0, 60, NULL),
            ('SICK',      N'إجازة مرضية',            'Sick Leave',         'SA', 120.0, 1, 100.00, 1, 3,  NULL),
            ('HAJJ',      N'إجازة الحج',             'Hajj Leave',         'SA', 15.0,  1, 100.00, 0, 12, NULL),
            ('MATERNITY', N'إجازة الأمومة',          'Maternity Leave',    'SA', 70.0,  1, 100.00, 0, 0,  'F'),
            ('PATERNITY', N'إجازة الأبوة',           'Paternity Leave',    'SA', 3.0,   1, 100.00, 0, 0,  'M'),
            ('BEREAVE',   N'إجازة وفاة',             'Bereavement Leave',  'SA', 5.0,   1, 100.00, 0, 0,  NULL),
            ('UNPAID',    N'إجازة بدون أجر',         'Unpaid Leave',       NULL, 0.0,  0, 0.00,   0, 0,  NULL),
            -- UAE: 30d annual, 60d maternity
            ('AE-ANNUAL', N'إجازة سنوية — الإمارات',  'Annual Leave (UAE)', 'AE', 30.0,  1, 100.00, 0, 6,  NULL),
            -- Jordan: 14d annual, 70d maternity
            ('JO-ANNUAL', N'إجازة سنوية — الأردن',    'Annual Leave (Jordan)','JO', 14.0, 1, 100.00, 0, 6,  NULL),
            -- Egypt: 21d annual
            ('EG-ANNUAL', N'إجازة سنوية — مصر',       'Annual Leave (Egypt)','EG', 21.0,  1, 100.00, 0, 6,  NULL)
        ) X (Code, NameAR, NameEN, CC, Days, Paid, PaidPct, NeedsAtt, MinSvc, Gender);
    END

    -- ==========================================================================
    -- COMPLETION
    -- ==========================================================================
    COMMIT TRANSACTION;
    PRINT N'═══════════════════════════════════════════════════════════════════════════';
    PRINT N'✅ Leave Management Extensions deployed successfully.';
    PRINT N'   - Types per labor law + computed balances + immutable accrual log.';
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
