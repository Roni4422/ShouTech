-- ========================================================================
-- FILE: dba/hr/att.sql
-- PROJECT: SHOUTECH ERP V10 - Attendance Scheduling Extensions (Ultimate 10/10)
-- VERSION: 10.3.0
-- DESCRIPTION: Work shifts & rosters, overtime requests, attendance
--              corrections (with approval workflow) and official holidays
--              calendar incl. Hijri-based Gulf holidays.
--              NOTE: dbo.Attendance (daily punches) lives in dba/hr/hr.sql.
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
PRINT N'SHOUTECH ERP V10 – Attendance Scheduling Extensions (Ultimate 10/10)';
PRINT N'═══════════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WorkShifts')
    BEGIN
        CREATE TABLE dbo.WorkShifts (
            WorkShiftID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ShiftCode NVARCHAR(20) NOT NULL,
            ShiftNameAR NVARCHAR(150) NOT NULL,
            ShiftNameEN NVARCHAR(150) NULL,
            StartTime TIME(0) NOT NULL,
            EndTime TIME(0) NOT NULL,
            CrossesMidnight BIT NOT NULL DEFAULT 0,
            BreakMinutes INT NOT NULL DEFAULT 0,
            ScheduledHours DECIMAL(4,2) NOT NULL,
            GraceLateMinutes INT NOT NULL DEFAULT 0,     -- سماحية التأخير
            GraceEarlyLeaveMinutes INT NOT NULL DEFAULT 0,
            IsFlexible BIT NOT NULL DEFAULT 0,
            WorkDaysMask NVARCHAR(7) NOT NULL DEFAULT '1111110', -- Sun..Sat (Gulf: Fri/Sat weekend)
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_WorkShifts PRIMARY KEY CLUSTERED (WorkShiftID),
            CONSTRAINT UQ_WorkShifts_Code UNIQUE (CompanyID, ShiftCode),
            CONSTRAINT CK_WorkShifts_Hours CHECK (ScheduledHours > 0 AND ScheduledHours <= 24)
        );
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ShiftAssignments')
    BEGIN
        CREATE TABLE dbo.ShiftAssignments (
            ShiftAssignmentID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            EmployeeID INT NOT NULL,
            WorkShiftID INT NOT NULL,
            ValidFrom DATE NOT NULL,
            ValidTo DATE NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_ShiftAssignments PRIMARY KEY CLUSTERED (ShiftAssignmentID),
            CONSTRAINT CK_ShiftAssignments_Validity CHECK (ValidTo IS NULL OR ValidTo >= ValidFrom)
        );
        CREATE NONCLUSTERED INDEX IX_ShiftAssignments_Emp
            ON dbo.ShiftAssignments (CompanyID, EmployeeID, ValidFrom) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'OvertimeRequests')
    BEGIN
        CREATE TABLE dbo.OvertimeRequests (
            OvertimeRequestID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            EmployeeID INT NOT NULL,
            RequestDate DATE NOT NULL,
            PlannedHours DECIMAL(4,2) NOT NULL,
            ActualHours DECIMAL(4,2) NULL,
            OvertimeType NVARCHAR(15) NOT NULL DEFAULT 'NORMAL', -- NORMAL | WEEKEND | HOLIDAY (rates differ)
            Reason NVARCHAR(500) NOT NULL,
            Status NVARCHAR(12) NOT NULL DEFAULT 'PENDING', -- PENDING | APPROVED | REJECTED | POSTED
            ApprovedBy UNIQUEIDENTIFIER NULL,
            ApprovedAt DATETIME2(7) NULL,
            RejectionReason NVARCHAR(300) NULL,
            PayrollRunID BIGINT NULL,                    -- Set when posted to payroll
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_OvertimeRequests PRIMARY KEY CLUSTERED (OvertimeRequestID),
            CONSTRAINT CK_OvertimeRequests_Status CHECK (Status IN ('PENDING','APPROVED','REJECTED','POSTED')),
            CONSTRAINT CK_OvertimeRequests_Hours CHECK (PlannedHours > 0 AND PlannedHours <= 16)
        );
        CREATE NONCLUSTERED INDEX IX_OvertimeRequests_Emp
            ON dbo.OvertimeRequests (CompanyID, EmployeeID, RequestDate) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    --    Never edit dbo.Attendance directly — corrections are audited rows.
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AttendanceCorrections')
    BEGIN
        CREATE TABLE dbo.AttendanceCorrections (
            CorrectionID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            EmployeeID INT NOT NULL,
            AttendanceDate DATE NOT NULL,
            CorrectionType NVARCHAR(15) NOT NULL,        -- FORGOT_IN | FORGOT_OUT | WRONG_TIME | ABSENT_TO_LEAVE
            CorrectedCheckIn TIME(0) NULL,
            CorrectedCheckOut TIME(0) NULL,
            Reason NVARCHAR(500) NOT NULL,
            AttachmentPath NVARCHAR(500) NULL,
            Status NVARCHAR(12) NOT NULL DEFAULT 'PENDING',
            ApprovedBy UNIQUEIDENTIFIER NULL,
            ApprovedAt DATETIME2(7) NULL,
            RejectionReason NVARCHAR(300) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_AttendanceCorrections PRIMARY KEY CLUSTERED (CorrectionID),
            CONSTRAINT CK_AttendanceCorrections_Type CHECK (CorrectionType IN ('FORGOT_IN','FORGOT_OUT','WRONG_TIME','ABSENT_TO_LEAVE')),
            CONSTRAINT CK_AttendanceCorrections_Status CHECK (Status IN ('PENDING','APPROVED','REJECTED'))
        );
        CREATE NONCLUSTERED INDEX IX_AttendanceCorrections_Emp
            ON dbo.AttendanceCorrections (CompanyID, EmployeeID, AttendanceDate) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'OfficialHolidays')
    BEGIN
        CREATE TABLE dbo.OfficialHolidays (
            HolidayID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            CountryCode NVARCHAR(2) NOT NULL,
            HolidayNameAR NVARCHAR(200) NOT NULL,
            HolidayNameEN NVARCHAR(200) NULL,
            HolidayDate DATE NULL,                       -- Fixed-date holidays
            HijriDate NVARCHAR(10) NULL,                 -- e.g. '01/01' for 1 Muharram (moon-sighting dependent)
            IsHijri BIT NOT NULL DEFAULT 0,
            DaysCount TINYINT NOT NULL DEFAULT 1,
            Year INT NOT NULL,
            IsPaid BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_OfficialHolidays PRIMARY KEY CLUSTERED (HolidayID),
            CONSTRAINT UQ_OfficialHolidays UNIQUE (CompanyID, CountryCode, Year, HolidayNameAR)
        );
        CREATE NONCLUSTERED INDEX IX_OfficialHolidays_Date
            ON dbo.OfficialHolidays (CompanyID, CountryCode, HolidayDate) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- COMPLETION
    -- ==========================================================================
    COMMIT TRANSACTION;
    PRINT N'═══════════════════════════════════════════════════════════════════════════';
    PRINT N'✅ Attendance Scheduling Extensions deployed successfully.';
    PRINT N'   - Shifts + rosters + overtime workflow + corrections + Hijri holidays.';
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
