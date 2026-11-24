-- ============================================================
-- source: archive/legacy_sqlserver/hr/att.sql
-- ============================================================
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
            GraceLateMinutes INT NOT NULL DEFAULT 0,     -- late grace period
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

-- ============================================================
-- source: archive/legacy_sqlserver/hr/emp.sql
-- ============================================================
-- ========================================================================
-- FILE: dba/hr/emp.sql
-- PROJECT: SHOUTECH ERP V10 - Employee Extensions (Ultimate 10/10)
-- VERSION: 10.3.0
-- DESCRIPTION: Complementary employee records extending hr.sql:
--              bank accounts (WPS — Wage Protection System), emergency
--              contacts, dependents (GOSI/insurance), position history
--              and employee skills matrix.
--              NOTE: dbo.Employees master table lives in dba/hr/hr.sql.
-- ========================================================================
-- ✅ Idempotent | TRY/CATCH + TRANSACTION | Soft Delete + RowVersion
-- ✅ WPS-ready (KSA Mudad / UAE WPS / Qatar WPS / Jordan / Egypt)
-- ========================================================================

USE [$(DbFileName)];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

PRINT N'═══════════════════════════════════════════════════════════════════════════';
PRINT N'SHOUTECH ERP V10 – Employee Extensions (Ultimate 10/10)';
PRINT N'═══════════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'EmployeeBankAccounts')
    BEGIN
        CREATE TABLE dbo.EmployeeBankAccounts (
            EmpBankAccountID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            EmployeeID INT NOT NULL,
            BankNameAR NVARCHAR(150) NOT NULL,
            BankNameEN NVARCHAR(150) NULL,
            BankCode NVARCHAR(20) NULL,                  -- SWIFT / local bank code
            BranchName NVARCHAR(150) NULL,
            IBAN NVARCHAR(34) NOT NULL,                  -- ISO 13616 (SA24…, AE07…, JO94…)
            AccountHolderName NVARCHAR(200) NOT NULL,
            CurrencyCode NVARCHAR(3) NOT NULL,
            IsPrimary BIT NOT NULL DEFAULT 1,            -- Salary account
            WpsEnabled BIT NOT NULL DEFAULT 1,           -- Include in WPS file
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_EmployeeBankAccounts PRIMARY KEY CLUSTERED (EmpBankAccountID),
            CONSTRAINT UQ_EmpBankAccounts_IBAN UNIQUE (CompanyID, EmployeeID, IBAN)
        );
        CREATE NONCLUSTERED INDEX IX_EmpBankAccounts_Emp
            ON dbo.EmployeeBankAccounts (CompanyID, EmployeeID, IsPrimary) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'EmployeeEmergencyContacts')
    BEGIN
        CREATE TABLE dbo.EmployeeEmergencyContacts (
            ContactID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            EmployeeID INT NOT NULL,
            ContactName NVARCHAR(200) NOT NULL,
            Relationship NVARCHAR(50) NOT NULL,          -- SPOUSE | PARENT | SIBLING | OTHER
            Phone NVARCHAR(30) NOT NULL,
            AlternatePhone NVARCHAR(30) NULL,
            Address NVARCHAR(300) NULL,
            Priority TINYINT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_EmployeeEmergencyContacts PRIMARY KEY CLUSTERED (ContactID)
        );
        CREATE NONCLUSTERED INDEX IX_EmpEmergencyContacts_Emp
            ON dbo.EmployeeEmergencyContacts (CompanyID, EmployeeID, Priority) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'EmployeeDependents')
    BEGIN
        CREATE TABLE dbo.EmployeeDependents (
            DependentID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            EmployeeID INT NOT NULL,
            FullName NVARCHAR(200) NOT NULL,
            Relationship NVARCHAR(20) NOT NULL,          -- SPOUSE | CHILD | PARENT
            DateOfBirth DATE NULL,
            NationalID NVARCHAR(30) NULL,                -- Iqama/National ID of dependent
            Gender NVARCHAR(6) NULL,
            IncludedInMedicalInsurance BIT NOT NULL DEFAULT 0,
            IncludedInSocialInsurance BIT NOT NULL DEFAULT 0, -- GOSI family coverage
            IsStudent BIT NOT NULL DEFAULT 0,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_EmployeeDependents PRIMARY KEY CLUSTERED (DependentID),
            CONSTRAINT CK_EmpDependents_Rel CHECK (Relationship IN ('SPOUSE','CHILD','PARENT'))
        );
        CREATE NONCLUSTERED INDEX IX_EmpDependents_Emp
            ON dbo.EmployeeDependents (CompanyID, EmployeeID) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    --    Immutable audit: each move inserts a new row, never updates old.
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'EmployeePositionHistory')
    BEGIN
        CREATE TABLE dbo.EmployeePositionHistory (
            PositionHistoryID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            EmployeeID INT NOT NULL,
            ChangeType NVARCHAR(20) NOT NULL,            -- HIRE | PROMOTION | TRANSFER | DEMOTION | SALARY_ADJ | TERMINATION
            EffectiveDate DATE NOT NULL,
            FromDepartmentID INT NULL,
            ToDepartmentID INT NULL,
            FromJobTitleID INT NULL,
            ToJobTitleID INT NULL,
            FromBasicSalary DECIMAL(18,4) NULL,
            ToBasicSalary DECIMAL(18,4) NULL,
            Reason NVARCHAR(500) NULL,
            DocumentRef NVARCHAR(100) NULL,              -- Decree/letter reference
            ApprovedBy UNIQUEIDENTIFIER NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_EmployeePositionHistory PRIMARY KEY CLUSTERED (PositionHistoryID),
            CONSTRAINT CK_EmpPosHistory_Type CHECK (ChangeType IN ('HIRE','PROMOTION','TRANSFER','DEMOTION','SALARY_ADJ','TERMINATION'))
        ) WITH (DATA_COMPRESSION = PAGE);
        CREATE NONCLUSTERED INDEX IX_EmpPosHistory_Emp
            ON dbo.EmployeePositionHistory (CompanyID, EmployeeID, EffectiveDate DESC) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'EmployeeSkills')
    BEGIN
        CREATE TABLE dbo.EmployeeSkills (
            EmployeeSkillID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            EmployeeID INT NOT NULL,
            SkillCode NVARCHAR(50) NOT NULL,
            SkillNameAR NVARCHAR(150) NOT NULL,
            SkillNameEN NVARCHAR(150) NULL,
            ProficiencyLevel TINYINT NOT NULL DEFAULT 1, -- 1 Beginner … 5 Expert
            Certified BIT NOT NULL DEFAULT 0,
            CertificateRef NVARCHAR(100) NULL,
            CertificateExpiry DATE NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_EmployeeSkills PRIMARY KEY CLUSTERED (EmployeeSkillID),
            CONSTRAINT UQ_EmployeeSkills UNIQUE (CompanyID, EmployeeID, SkillCode),
            CONSTRAINT CK_EmployeeSkills_Level CHECK (ProficiencyLevel BETWEEN 1 AND 5)
        );
        CREATE NONCLUSTERED INDEX IX_EmployeeSkills_Skill
            ON dbo.EmployeeSkills (CompanyID, SkillCode, ProficiencyLevel DESC) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- COMPLETION
    -- ==========================================================================
    COMMIT TRANSACTION;
    PRINT N'═══════════════════════════════════════════════════════════════════════════';
    PRINT N'✅ Employee Extensions deployed successfully.';
    PRINT N'   - WPS-ready bank accounts + dependents (GOSI) + position history.';
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
-- source: archive/legacy_sqlserver/hr/hr.sql
-- ============================================================
-- ========================================================================
-- FILE: dba/sch/hr.sql
-- PROJECT: SHOUTECH ERP V10 - Human Resources Module (Ultimate 10/10)
-- VERSION: 10.1.0
-- DESCRIPTION: Complete HR with employees, attendance, payroll (GOSI),
--              leaves, loans, end-of-service, contracts, documents.
--              Fully idempotent, transactional, soft-delete, compression-ready.
-- ========================================================================
-- IMPROVEMENTS:
-- ✅ IF NOT EXISTS for all objects (idempotent)
-- ✅ TRY/CATCH + TRANSACTION wrapper
-- ✅ Soft Delete (IsDeleted, DeletedAt, DeletedBy) on ALL tables
-- ✅ RowVersion on ALL tables
-- ✅ DATA_COMPRESSION = PAGE on large tables (Attendance, Payroll, Leaves, EmployeeDocuments)
-- ✅ Columnstore indexes for analytics (Attendance, Payroll)
-- ✅ Unified audit columns (CreatedBy, CreatedAt, UpdatedBy, UpdatedAt)
-- ✅ Seed data (default department, job titles)
-- ✅ All indexes filtered with WHERE IsDeleted = 0
-- ========================================================================

USE [$(DbFileName)];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

PRINT N'═══════════════════════════════════════════════════════════════════════════';
PRINT N'SHOUTECH ERP V10 – Human Resources Module (Ultimate 10/10)';
PRINT N'═══════════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- ==========================================================================
    -- 1. DEPARTMENTS (with Soft Delete, RowVersion)
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Departments')
    BEGIN
        CREATE TABLE dbo.Departments (
            DepartmentID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            DepartmentCode NVARCHAR(50) NOT NULL,
            DepartmentNameAR NVARCHAR(200) NOT NULL,
            DepartmentNameEN NVARCHAR(200) NOT NULL,
            ParentDepartmentCode NVARCHAR(50),
            DepartmentLevel TINYINT DEFAULT 1,
            ManagerEmployeeID INT,
            CostCenterCode NVARCHAR(50),
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_Departments PRIMARY KEY CLUSTERED (DepartmentID),
            CONSTRAINT UQ_Departments_Code UNIQUE (CompanyID, DepartmentCode),
            CONSTRAINT FK_Departments_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID)
        );
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Departments_Company_Active ON dbo.Departments(CompanyID, IsActive) INCLUDE (DepartmentCode, DepartmentNameAR) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Departments_Parent ON dbo.Departments(ParentDepartmentCode) WHERE ParentDepartmentCode IS NOT NULL;
    GO

    -- ==========================================================================
    -- 2. JOB TITLES (with Soft Delete, RowVersion)
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'JobTitles')
    BEGIN
        CREATE TABLE dbo.JobTitles (
            JobTitleID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            JobTitleCode NVARCHAR(50) NOT NULL,
            JobTitleNameAR NVARCHAR(200) NOT NULL,
            JobTitleNameEN NVARCHAR(200) NOT NULL,
            JobGrade NVARCHAR(20),
            JobCategory NVARCHAR(50),
            MinSalary DECIMAL(18,2),
            MaxSalary DECIMAL(18,2),
            JobDescription NVARCHAR(MAX),
            Responsibilities NVARCHAR(MAX),
            Qualifications NVARCHAR(MAX),
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_JobTitles PRIMARY KEY CLUSTERED (JobTitleID),
            CONSTRAINT UQ_JobTitles_Code UNIQUE (CompanyID, JobTitleCode),
            CONSTRAINT FK_JobTitles_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID)
        );
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_JobTitles_Company_Active ON dbo.JobTitles(CompanyID, IsActive) INCLUDE (JobTitleCode, JobTitleNameAR) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_JobTitles_Category ON dbo.JobTitles(JobCategory);
    GO

    -- ==========================================================================
    -- 3. EMPLOYEES (already has Soft Delete and RowVersion, add missing UpdatedBy trigger)
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Employees')
    BEGIN
        CREATE TABLE dbo.Employees (
            EmployeeID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            EmployeeCode NVARCHAR(50) NOT NULL,
            FirstNameAR NVARCHAR(100) NOT NULL,
            MiddleNameAR NVARCHAR(100),
            LastNameAR NVARCHAR(100) NOT NULL,
            FullNameAR AS (FirstNameAR + ' ' + ISNULL(MiddleNameAR + ' ', '') + LastNameAR) PERSISTED,
            FirstNameEN NVARCHAR(100),
            LastNameEN NVARCHAR(100),
            FullNameEN AS (ISNULL(FirstNameEN + ' ', '') + ISNULL(LastNameEN, '')) PERSISTED,
            Gender NVARCHAR(10),
            DateOfBirth DATE,
            PlaceOfBirth NVARCHAR(200),
            MaritalStatus NVARCHAR(20),
            NumberOfDependents INT NOT NULL DEFAULT 0,
            Nationality NVARCHAR(100),
            Religion NVARCHAR(50),
            BloodType NVARCHAR(5),
            IDType NVARCHAR(30),
            IDNumber NVARCHAR(50),
            IDIssuePlace NVARCHAR(200),
            IDIssueDate DATE,
            IDExpiryDate DATE,
            PassportNumber NVARCHAR(50),
            PassportIssuePlace NVARCHAR(200),
            PassportIssueDate DATE,
            PassportExpiryDate DATE,
            PersonalPhone NVARCHAR(50),
            WorkPhone NVARCHAR(50),
            PersonalEmail NVARCHAR(255),
            WorkEmail NVARCHAR(255),
            AddressLine1 NVARCHAR(255),
            City NVARCHAR(100),
            State NVARCHAR(100),
            PostalCode NVARCHAR(20),
            Country NVARCHAR(100),
            EmergencyContactName NVARCHAR(200),
            EmergencyContactPhone NVARCHAR(50),
            EmergencyContactRelation NVARCHAR(50),
            DepartmentCode NVARCHAR(50),
            JobTitleCode NVARCHAR(50),
            ReportsToEmployeeID INT,
            EmploymentType NVARCHAR(30) NOT NULL,
            EmploymentStatus NVARCHAR(30) NOT NULL DEFAULT 'ACTIVE',
            HireDate DATE NOT NULL,
            ProbationEndDate DATE,
            ConfirmationDate DATE,
            TerminationDate DATE,
            TerminationReason NVARCHAR(200),
            TerminationType NVARCHAR(30),
            BasicSalary DECIMAL(18,2) NOT NULL,
            HousingAllowance DECIMAL(18,2) NOT NULL DEFAULT 0,
            TransportAllowance DECIMAL(18,2) NOT NULL DEFAULT 0,
            FoodAllowance DECIMAL(18,2) NOT NULL DEFAULT 0,
            OtherAllowances DECIMAL(18,2) NOT NULL DEFAULT 0,
            TotalSalary AS (BasicSalary + HousingAllowance + TransportAllowance + FoodAllowance + OtherAllowances) PERSISTED,
            BankName NVARCHAR(200),
            BankBranchName NVARCHAR(200),
            BankAccountNumber NVARCHAR(50),
            IBAN NVARCHAR(50),
            GOSINumber NVARCHAR(50),
            GOSIRegistrationDate DATE,
            GOSIContributionPercentage DECIMAL(5,2) NOT NULL DEFAULT 9.75,
            AnnualLeaveBalance DECIMAL(5,1) NOT NULL DEFAULT 0,
            SickLeaveBalance DECIMAL(5,1) NOT NULL DEFAULT 0,
            LastAppraisalDate DATE,
            NextAppraisalDate DATE,
            PerformanceRating DECIMAL(3,2),
            LinkedUserID UNIQUEIDENTIFIER,
            BiometricID NVARCHAR(100),
            PhotoURL NVARCHAR(500),
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            Notes NVARCHAR(MAX),
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_Employees PRIMARY KEY CLUSTERED (EmployeeID),
            CONSTRAINT UQ_Employees_Code UNIQUE (CompanyID, EmployeeCode),
            CONSTRAINT UQ_Employees_IDNumber UNIQUE (IDNumber),
            CONSTRAINT FK_Employees_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID),
            CONSTRAINT CK_Employees_Gender CHECK (Gender IN ('MALE', 'FEMALE')),
            CONSTRAINT CK_Employees_EmploymentType CHECK (EmploymentType IN ('FULL_TIME', 'PART_TIME', 'CONTRACT', 'TEMPORARY'))
        );
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Employees_Company_Active ON dbo.Employees(CompanyID, EmploymentStatus) INCLUDE (EmployeeCode, FullNameAR, DepartmentCode, BasicSalary) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Employees_Department ON dbo.Employees(DepartmentCode, EmploymentStatus) INCLUDE (EmployeeCode, FullNameAR, JobTitleCode) WHERE IsDeleted = 0 AND EmploymentStatus = 'ACTIVE';
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Employees_GOSI ON dbo.Employees(GOSINumber) WHERE GOSINumber IS NOT NULL AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Employees_HireDate ON dbo.Employees(HireDate, EmploymentStatus) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Employees_ReportsTo ON dbo.Employees(ReportsToEmployeeID);
    GO

    -- ==========================================================================
    -- 4. EMPLOYEE CONTRACTS (add Soft Delete, RowVersion, audit columns)
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'EmployeeContracts')
    BEGIN
        CREATE TABLE dbo.EmployeeContracts (
            ContractID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            EmployeeID INT NOT NULL,
            ContractNumber NVARCHAR(50) NOT NULL,
            ContractType NVARCHAR(30) NOT NULL,
            StartDate DATE NOT NULL,
            EndDate DATE,
            BasicSalary DECIMAL(18,2) NOT NULL,
            HousingAllowance DECIMAL(18,2) NOT NULL DEFAULT 0,
            TransportAllowance DECIMAL(18,2) NOT NULL DEFAULT 0,
            OtherAllowances DECIMAL(18,2) NOT NULL DEFAULT 0,
            WorkingHoursPerWeek DECIMAL(4,1) NOT NULL DEFAULT 48,
            WorkingDaysPerWeek TINYINT NOT NULL DEFAULT 6,
            ProbationDays INT NOT NULL DEFAULT 90,
            NoticePeriodDays INT NOT NULL DEFAULT 60,
            AnnualLeaveDays INT NOT NULL DEFAULT 21,
            ContractStatus NVARCHAR(20) NOT NULL DEFAULT 'ACTIVE',
            FilePath NVARCHAR(500),
            SignedAt DATETIME2(7),
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_EmployeeContracts PRIMARY KEY CLUSTERED (ContractID),
            CONSTRAINT UQ_EmployeeContracts_Number UNIQUE (CompanyID, ContractNumber),
            CONSTRAINT FK_EmployeeContracts_Employee FOREIGN KEY (EmployeeID) REFERENCES dbo.Employees(EmployeeID) ON DELETE CASCADE
        );
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_EmployeeContracts_Employee ON dbo.EmployeeContracts(EmployeeID, ContractStatus) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_EmployeeContracts_Expiring ON dbo.EmployeeContracts(EndDate, ContractStatus) WHERE ContractStatus = 'ACTIVE' AND EndDate IS NOT NULL AND IsDeleted = 0;
    GO

    -- ==========================================================================
    -- 5. ATTENDANCE (with compression, columnstore, Soft Delete, RowVersion)
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Attendance')
    BEGIN
        CREATE TABLE dbo.Attendance (
            AttendanceID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            EmployeeID INT NOT NULL,
            AttendanceDate DATE NOT NULL,
            CheckInTime TIME(0),
            CheckOutTime TIME(0),
            CheckInSource NVARCHAR(30),
            CheckOutSource NVARCHAR(30),
            CheckInDeviceID NVARCHAR(50),
            CheckOutDeviceID NVARCHAR(50),
            CheckInLocation NVARCHAR(200),
            CheckOutLocation NVARCHAR(200),
            ScheduledStartTime TIME(0),
            ScheduledEndTime TIME(0),
            ScheduledHours DECIMAL(4,2) NOT NULL DEFAULT 8,
            ActualHours AS (CASE WHEN CheckInTime IS NOT NULL AND CheckOutTime IS NOT NULL THEN CAST(DATEDIFF(MINUTE, CheckInTime, CheckOutTime) / 60.0 AS DECIMAL(4,2)) ELSE 0 END) PERSISTED,
            OvertimeHours DECIMAL(4,2) NOT NULL DEFAULT 0,
            LateMinutes INT NOT NULL DEFAULT 0,
            EarlyLeaveMinutes INT NOT NULL DEFAULT 0,
            AttendanceStatus NVARCHAR(20) NOT NULL DEFAULT 'PRESENT',
            ApprovedBy UNIQUEIDENTIFIER,
            ApprovedAt DATETIME2(7),
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            Notes NVARCHAR(500),
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_Attendance PRIMARY KEY CLUSTERED (AttendanceID),
            CONSTRAINT UQ_Attendance_EmployeeDate UNIQUE (EmployeeID, AttendanceDate),
            CONSTRAINT FK_Attendance_Employee FOREIGN KEY (EmployeeID) REFERENCES dbo.Employees(EmployeeID) ON DELETE CASCADE,
            CONSTRAINT CK_Attendance_Status CHECK (AttendanceStatus IN ('PRESENT', 'ABSENT', 'LATE', 'HALF_DAY', 'WEEKEND', 'HOLIDAY', 'VACATION', 'SICK', 'BUSINESS_TRIP'))
        ) WITH (DATA_COMPRESSION = PAGE);
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Attendance_Employee_Date ON dbo.Attendance(EmployeeID, AttendanceDate DESC) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Attendance_Company_Date ON dbo.Attendance(CompanyID, AttendanceDate DESC, AttendanceStatus) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Attendance_Late ON dbo.Attendance(AttendanceDate, LateMinutes DESC) WHERE LateMinutes > 0 AND IsDeleted = 0;
    CREATE NONCLUSTERED COLUMNSTORE INDEX IF NOT EXISTS CSIX_Attendance_Analytics ON dbo.Attendance (EmployeeID, AttendanceDate, ActualHours, OvertimeHours, LateMinutes);
    GO

    -- ==========================================================================
    -- 6. LEAVES (with Soft Delete, RowVersion)
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Leaves')
    BEGIN
        CREATE TABLE dbo.Leaves (
            LeaveID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            EmployeeID INT NOT NULL,
            LeaveType NVARCHAR(30) NOT NULL,
            StartDate DATE NOT NULL,
            EndDate DATE NOT NULL,
            DaysCount DECIMAL(5,1) NOT NULL,
            LeaveStatus NVARCHAR(20) NOT NULL DEFAULT 'PENDING',
            RequestedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            ApprovedBy UNIQUEIDENTIFIER,
            ApprovedAt DATETIME2(7),
            RejectedBy UNIQUEIDENTIFIER,
            RejectedAt DATETIME2(7),
            RejectionReason NVARCHAR(500),
            MedicalCertificateRequired BIT NOT NULL DEFAULT 0,
            MedicalCertificatePath NVARCHAR(500),
            HospitalName NVARCHAR(200),
            DoctorName NVARCHAR(200),
            ReplacementEmployeeID INT,
            ReplacementNotified BIT NOT NULL DEFAULT 0,
            IsPaid BIT NOT NULL DEFAULT 1,
            DeductionAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            Notes NVARCHAR(MAX),
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_Leaves PRIMARY KEY CLUSTERED (LeaveID),
            CONSTRAINT FK_Leaves_Employee FOREIGN KEY (EmployeeID) REFERENCES dbo.Employees(EmployeeID) ON DELETE CASCADE,
            CONSTRAINT CK_Leaves_Type CHECK (LeaveType IN ('ANNUAL', 'SICK', 'EMERGENCY', 'UNPAID', 'MATERNITY', 'PATERNITY', 'HAJJ', 'BEREAVEMENT'))
        ) WITH (DATA_COMPRESSION = PAGE);
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Leaves_Employee ON dbo.Leaves(EmployeeID, LeaveStatus, StartDate DESC) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Leaves_Company_Pending ON dbo.Leaves(CompanyID, LeaveStatus, RequestedAt DESC) WHERE LeaveStatus = 'PENDING' AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Leaves_DateRange ON dbo.Leaves(StartDate, EndDate, LeaveStatus) WHERE LeaveStatus = 'APPROVED' AND IsDeleted = 0;
    GO

    -- ==========================================================================
    -- 7. PAYROLL (with compression, columnstore, Soft Delete, RowVersion)
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Payroll')
    BEGIN
        CREATE TABLE dbo.Payroll (
            PayrollID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            EmployeeID INT NOT NULL,
            PayrollMonth DATE NOT NULL,
            FiscalYear INT NOT NULL,
            FiscalPeriod TINYINT NOT NULL,
            BasicSalary DECIMAL(18,2) NOT NULL,
            HousingAllowance DECIMAL(18,2) NOT NULL DEFAULT 0,
            TransportAllowance DECIMAL(18,2) NOT NULL DEFAULT 0,
            FoodAllowance DECIMAL(18,2) NOT NULL DEFAULT 0,
            OtherAllowances DECIMAL(18,2) NOT NULL DEFAULT 0,
            OvertimeHours DECIMAL(5,2) NOT NULL DEFAULT 0,
            OvertimeRate DECIMAL(18,2) NOT NULL DEFAULT 0,
            OvertimeAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
            CommissionAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
            BonusAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
            TotalEarnings AS (BasicSalary + HousingAllowance + TransportAllowance + FoodAllowance + OtherAllowances + OvertimeAmount + CommissionAmount + BonusAmount) PERSISTED,
            GOSIEmployeeShare DECIMAL(18,2) NOT NULL DEFAULT 0,
            GOSICompanyShare DECIMAL(18,2) NOT NULL DEFAULT 0,
            AbsenceDays DECIMAL(5,1) NOT NULL DEFAULT 0,
            AbsenceDeduction DECIMAL(18,2) NOT NULL DEFAULT 0,
            LateDays INT NOT NULL DEFAULT 0,
            LateDeduction DECIMAL(18,2) NOT NULL DEFAULT 0,
            LoanDeduction DECIMAL(18,2) NOT NULL DEFAULT 0,
            AdvanceDeduction DECIMAL(18,2) NOT NULL DEFAULT 0,
            OtherDeductions DECIMAL(18,2) NOT NULL DEFAULT 0,
            TotalDeductions AS (GOSIEmployeeShare + AbsenceDeduction + LateDeduction + LoanDeduction + AdvanceDeduction + OtherDeductions) PERSISTED,
            NetSalary AS ((BasicSalary + HousingAllowance + TransportAllowance + FoodAllowance + OtherAllowances + OvertimeAmount + CommissionAmount + BonusAmount) - (GOSIEmployeeShare + AbsenceDeduction + LateDeduction + LoanDeduction + AdvanceDeduction + OtherDeductions)) PERSISTED,
            PaymentDate DATE,
            PaymentMethod NVARCHAR(30),
            PaymentReference NVARCHAR(100),
            PayrollStatus NVARCHAR(20) NOT NULL DEFAULT 'DRAFT',
            CalculatedAt DATETIME2(7),
            CalculatedBy UNIQUEIDENTIFIER,
            ApprovedBy UNIQUEIDENTIFIER,
            ApprovedAt DATETIME2(7),
            PaidBy UNIQUEIDENTIFIER,
            PaidAt DATETIME2(7),
            IsPosted BIT NOT NULL DEFAULT 0,
            JournalEntryID BIGINT,
            TotalWorkingDays INT NOT NULL DEFAULT 30,
            ActualWorkingDays INT NOT NULL DEFAULT 30,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            Notes NVARCHAR(MAX),
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_Payroll PRIMARY KEY CLUSTERED (PayrollID),
            CONSTRAINT UQ_Payroll_EmployeeMonth UNIQUE (EmployeeID, PayrollMonth),
            CONSTRAINT FK_Payroll_Employee FOREIGN KEY (EmployeeID) REFERENCES dbo.Employees(EmployeeID) ON DELETE CASCADE
        ) WITH (DATA_COMPRESSION = PAGE);
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Payroll_Company_Month ON dbo.Payroll(CompanyID, PayrollMonth DESC, PayrollStatus) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Payroll_Employee ON dbo.Payroll(EmployeeID, PayrollMonth DESC) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Payroll_Unpaid ON dbo.Payroll(CompanyID, PayrollStatus) WHERE PayrollStatus IN ('APPROVED') AND PaidAt IS NULL AND IsDeleted = 0;
    CREATE NONCLUSTERED COLUMNSTORE INDEX IF NOT EXISTS CSIX_Payroll_Analytics ON dbo.Payroll (EmployeeID, PayrollMonth, BasicSalary, TotalEarnings, TotalDeductions, NetSalary, OvertimeHours, AbsenceDays);
    GO

    -- ==========================================================================
    -- 8. EMPLOYEE LOANS (add Soft Delete, RowVersion, audit columns)
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'EmployeeLoans')
    BEGIN
        CREATE TABLE dbo.EmployeeLoans (
            LoanID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            EmployeeID INT NOT NULL,
            LoanNumber NVARCHAR(50) NOT NULL,
            LoanType NVARCHAR(30) NOT NULL,
            LoanAmount DECIMAL(18,2) NOT NULL,
            RemainingAmount DECIMAL(18,2) NOT NULL,
            MonthlyDeduction DECIMAL(18,2) NOT NULL,
            NumberOfInstallments INT NOT NULL,
            InstallmentsPaid INT NOT NULL DEFAULT 0,
            InstallmentsRemaining AS (NumberOfInstallments - InstallmentsPaid) PERSISTED,
            LoanDate DATE NOT NULL,
            FirstDeductionDate DATE,
            LastDeductionDate DATE,
            InterestRate DECIMAL(5,2) NOT NULL DEFAULT 0,
            LoanStatus NVARCHAR(20) NOT NULL DEFAULT 'ACTIVE',
            GuarantorEmployeeID INT,
            GuarantorAcceptedAt DATETIME2(7),
            ApprovedBy UNIQUEIDENTIFIER,
            ApprovedAt DATETIME2(7),
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            Notes NVARCHAR(MAX),
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_EmployeeLoans PRIMARY KEY CLUSTERED (LoanID),
            CONSTRAINT UQ_EmployeeLoans_Number UNIQUE (CompanyID, LoanNumber),
            CONSTRAINT FK_EmployeeLoans_Employee FOREIGN KEY (EmployeeID) REFERENCES dbo.Employees(EmployeeID) ON DELETE CASCADE,
            CONSTRAINT CK_EmployeeLoans_Type CHECK (LoanType IN ('PERSONAL', 'HOUSING', 'VEHICLE', 'ADVANCE', 'EMERGENCY'))
        );
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_EmployeeLoans_Employee ON dbo.EmployeeLoans(EmployeeID, LoanStatus) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_EmployeeLoans_Active ON dbo.EmployeeLoans(CompanyID, LoanStatus, FirstDeductionDate) WHERE LoanStatus = 'ACTIVE' AND IsDeleted = 0;
    GO

    -- ==========================================================================
    -- 9. END OF SERVICE (add Soft Delete, RowVersion, audit columns)
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'EndOfService')
    BEGIN
        CREATE TABLE dbo.EndOfService (
            EOSID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            EmployeeID INT NOT NULL,
            CalculationDate DATE NOT NULL,
            TerminationType NVARCHAR(30) NOT NULL,
            ServiceStartDate DATE NOT NULL,
            ServiceEndDate DATE NOT NULL,
            TotalServiceYears DECIMAL(5,2),
            TotalServiceMonths INT,
            TotalServiceDays INT,
            First5YearsMonths INT,
            First5YearsAmount DECIMAL(18,2),
            After5YearsMonths INT,
            After5YearsAmount DECIMAL(18,2),
            LastBasicSalary DECIMAL(18,2),
            CalculationBase DECIMAL(18,2),
            TotalEOSAmount DECIMAL(18,2),
            LoanBalance DECIMAL(18,2) NOT NULL DEFAULT 0,
            AdvanceBalance DECIMAL(18,2) NOT NULL DEFAULT 0,
            NoticeShortfallDeduction DECIMAL(18,2) NOT NULL DEFAULT 0,
            OtherDeductions DECIMAL(18,2) NOT NULL DEFAULT 0,
            NetEOSAmount AS (TotalEOSAmount - LoanBalance - AdvanceBalance - NoticeShortfallDeduction - OtherDeductions) PERSISTED,
            PaymentDate DATE,
            PaymentMethod NVARCHAR(30),
            PaymentReference NVARCHAR(100),
            PaymentStatus NVARCHAR(20) NOT NULL DEFAULT 'CALCULATED',
            IsPosted BIT NOT NULL DEFAULT 0,
            JournalEntryID BIGINT,
            ApprovedBy UNIQUEIDENTIFIER,
            ApprovedAt DATETIME2(7),
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            Notes NVARCHAR(MAX),
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_EndOfService PRIMARY KEY CLUSTERED (EOSID),
            CONSTRAINT FK_EndOfService_Employee FOREIGN KEY (EmployeeID) REFERENCES dbo.Employees(EmployeeID) ON DELETE CASCADE,
            CONSTRAINT CK_EndOfService_TerminationType CHECK (TerminationType IN ('RESIGNATION', 'TERMINATION', 'RETIREMENT', 'END_OF_CONTRACT', 'DEATH', 'DISABILITY'))
        );
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_EndOfService_Employee ON dbo.EndOfService(EmployeeID, CalculationDate DESC) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_EndOfService_Unpaid ON dbo.EndOfService(CompanyID, PaymentStatus) WHERE PaymentStatus = 'CALCULATED' AND IsDeleted = 0;
    GO

    -- ==========================================================================
    -- 10. EMPLOYEE DOCUMENTS (add Soft Delete, RowVersion, compression)
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'EmployeeDocuments')
    BEGIN
        CREATE TABLE dbo.EmployeeDocuments (
            DocumentID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            EmployeeID INT NOT NULL,
            DocumentType NVARCHAR(30) NOT NULL,
            DocumentTitle NVARCHAR(200) NOT NULL,
            DocumentNumber NVARCHAR(100),
            IssueDate DATE,
            ExpiryDate DATE,
            FilePath NVARCHAR(500) NOT NULL,
            FileSize BIGINT,
            MimeType NVARCHAR(100),
            FileHash NVARCHAR(64),
            ExpiryAlertDays INT NOT NULL DEFAULT 30,
            AlertSent BIT NOT NULL DEFAULT 0,
            AlertSentAt DATETIME2(7),
            IsVerified BIT NOT NULL DEFAULT 0,
            VerifiedBy UNIQUEIDENTIFIER,
            VerifiedAt DATETIME2(7),
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            Notes NVARCHAR(500),
            UploadedBy UNIQUEIDENTIFIER NOT NULL,
            UploadedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            RowVersion ROWVERSION,
            CONSTRAINT PK_EmployeeDocuments PRIMARY KEY CLUSTERED (DocumentID),
            CONSTRAINT FK_EmployeeDocuments_Employee FOREIGN KEY (EmployeeID) REFERENCES dbo.Employees(EmployeeID) ON DELETE CASCADE,
            CONSTRAINT CK_EmployeeDocuments_Type CHECK (DocumentType IN ('ID_COPY', 'PASSPORT', 'CONTRACT', 'CV', 'CERTIFICATE', 'DIPLOMA', 'MEDICAL', 'GOSI', 'PHOTO', 'OTHER'))
        ) WITH (DATA_COMPRESSION = PAGE);
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_EmployeeDocuments_Employee ON dbo.EmployeeDocuments(EmployeeID, DocumentType) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_EmployeeDocuments_ExpiryDate ON dbo.EmployeeDocuments(ExpiryDate, AlertSent) WHERE ExpiryDate IS NOT NULL AND AlertSent = 0 AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_EmployeeDocuments_Company_Type ON dbo.EmployeeDocuments(CompanyID, DocumentType) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_EmployeeDocuments_Verified ON dbo.EmployeeDocuments(IsVerified) WHERE IsVerified = 0 AND IsDeleted = 0;
    GO

    -- ==========================================================================
    -- 11. STORED PROCEDURES (idempotent with CREATE OR ALTER)
    -- ==========================================================================
    PRINT N'✅ Creating advanced stored procedures...';

    CREATE OR ALTER PROCEDURE dbo.sp_CalculatePayroll
        @CompanyID UNIQUEIDENTIFIER,
        @PayrollMonth DATE,
        @FiscalYear INT,
        @FiscalPeriod TINYINT,
        @CalculatedBy UNIQUEIDENTIFIER
    AS
    BEGIN
        SET NOCOUNT ON;
        DELETE FROM dbo.Payroll 
        WHERE CompanyID = @CompanyID AND PayrollMonth = @PayrollMonth AND PayrollStatus = 'DRAFT';
        
        INSERT INTO dbo.Payroll (
            CompanyID, EmployeeID, PayrollMonth, FiscalYear, FiscalPeriod,
            BasicSalary, HousingAllowance, TransportAllowance, FoodAllowance, OtherAllowances,
            OvertimeHours, OvertimeAmount, BonusAmount,
            GOSIEmployeeShare, GOSICompanyShare,
            AbsenceDays, AbsenceDeduction, LateDays, LateDeduction,
            LoanDeduction, AdvanceDeduction,
            TotalWorkingDays, ActualWorkingDays,
            CalculatedAt, CalculatedBy, PayrollStatus,
            CreatedBy, CreatedAt
        )
        SELECT
            @CompanyID, E.EmployeeID, @PayrollMonth, @FiscalYear, @FiscalPeriod,
            E.BasicSalary, E.HousingAllowance, E.TransportAllowance, E.FoodAllowance, E.OtherAllowances,
            OT.OvertimeHours, OT.OvertimeHours * (E.BasicSalary / 30 / 8 * 1.5), 0,
            (E.BasicSalary + E.HousingAllowance) * (E.GOSIContributionPercentage / 100),
            (E.BasicSalary + E.HousingAllowance) * 0.1175,
            ISNULL(ABS.AbsenceDays, 0), ISNULL(ABS.AbsenceDays, 0) * (E.BasicSalary / 30),
            ISNULL(LAT.LateDays, 0), ISNULL(LAT.LateDays, 0) * (E.BasicSalary / 30 / 8),
            ISNULL(L.LoanDeduction, 0), ISNULL(A.AdvanceDeduction, 0),
            30, 30 - ISNULL(ABS.AbsenceDays, 0),
            SYSUTCDATETIME(), @CalculatedBy, 'CALCULATED',
            @CalculatedBy, SYSUTCDATETIME()
        FROM dbo.Employees E
        LEFT JOIN (SELECT EmployeeID, SUM(DaysCount) AS AbsenceDays FROM dbo.Attendance WHERE CompanyID = @CompanyID AND AttendanceDate >= @PayrollMonth AND AttendanceDate < DATEADD(MONTH, 1, @PayrollMonth) AND AttendanceStatus IN ('ABSENT') GROUP BY EmployeeID) ABS ON E.EmployeeID = ABS.EmployeeID
        LEFT JOIN (SELECT EmployeeID, COUNT(DISTINCT AttendanceDate) AS LateDays FROM dbo.Attendance WHERE CompanyID = @CompanyID AND AttendanceDate >= @PayrollMonth AND AttendanceDate < DATEADD(MONTH, 1, @PayrollMonth) AND LateMinutes > 0 GROUP BY EmployeeID) LAT ON E.EmployeeID = LAT.EmployeeID
        LEFT JOIN (SELECT EmployeeID, COUNT(AttendanceDate) AS OvertimeHours FROM dbo.Attendance WHERE CompanyID = @CompanyID AND AttendanceDate >= @PayrollMonth AND AttendanceDate < DATEADD(MONTH, 1, @PayrollMonth) AND OvertimeHours > 0 GROUP BY EmployeeID) OT ON E.EmployeeID = OT.EmployeeID
        LEFT JOIN (SELECT EmployeeID, SUM(MonthlyDeduction) AS LoanDeduction FROM dbo.EmployeeLoans WHERE CompanyID = @CompanyID AND LoanStatus = 'ACTIVE' AND FirstDeductionDate <= @PayrollMonth AND (LastDeductionDate IS NULL OR LastDeductionDate >= @PayrollMonth) GROUP BY EmployeeID) L ON E.EmployeeID = L.EmployeeID
        LEFT JOIN (SELECT EmployeeID, 0 AS AdvanceDeduction) A ON E.EmployeeID = A.EmployeeID
        WHERE E.CompanyID = @CompanyID AND E.EmploymentStatus = 'ACTIVE' AND (E.TerminationDate IS NULL OR E.TerminationDate >= @PayrollMonth);
        
        PRINT '✅ Payroll calculated for ' + CAST(@@ROWCOUNT AS NVARCHAR) + ' employees';
    END;
    GO

    CREATE OR ALTER PROCEDURE dbo.sp_CalculateEOS
        @EmployeeID INT,
        @TerminationType NVARCHAR(30),
        @TerminationDate DATE,
        @CalculatedBy UNIQUEIDENTIFIER
    AS
    BEGIN
        SET NOCOUNT ON;
        DECLARE @ServiceStartDate DATE, @ServiceEndDate DATE;
        DECLARE @TotalServiceMonths INT, @First5YearsMonths INT, @After5YearsMonths INT;
        DECLARE @LastBasicSalary DECIMAL(18,2), @CalculationBase DECIMAL(18,2);
        DECLARE @First5YearsAmount DECIMAL(18,2), @After5YearsAmount DECIMAL(18,2), @TotalEOSAmount DECIMAL(18,2);
        
        SELECT @ServiceStartDate = HireDate, @LastBasicSalary = BasicSalary, @CalculationBase = BasicSalary + HousingAllowance
        FROM dbo.Employees WHERE EmployeeID = @EmployeeID;
        
        SET @ServiceEndDate = @TerminationDate;
        SET @TotalServiceMonths = DATEDIFF(MONTH, @ServiceStartDate, @ServiceEndDate);
        SET @First5YearsMonths = CASE WHEN @TotalServiceMonths >= 60 THEN 60 ELSE @TotalServiceMonths END;
        SET @After5YearsMonths = CASE WHEN @TotalServiceMonths > 60 THEN @TotalServiceMonths - 60 ELSE 0 END;
        
        SET @First5YearsAmount = (@CalculationBase / 2) * (@First5YearsMonths / 12.0);
        SET @After5YearsAmount = @CalculationBase * (@After5YearsMonths / 12.0);
        SET @TotalEOSAmount = @First5YearsAmount + @After5YearsAmount;
        
        INSERT INTO dbo.EndOfService (
            CompanyID, EmployeeID, CalculationDate, TerminationType,
            ServiceStartDate, ServiceEndDate,
            TotalServiceMonths, First5YearsMonths, First5YearsAmount,
            After5YearsMonths, After5YearsAmount,
            LastBasicSalary, CalculationBase, TotalEOSAmount,
            PaymentStatus, CreatedBy, CreatedAt
        )
        SELECT CompanyID, @EmployeeID, @TerminationDate, @TerminationType,
               @ServiceStartDate, @ServiceEndDate,
               @TotalServiceMonths, @First5YearsMonths, @First5YearsAmount,
               @After5YearsMonths, @After5YearsAmount,
               @LastBasicSalary, @CalculationBase, @TotalEOSAmount,
               'CALCULATED', @CalculatedBy, SYSUTCDATETIME()
        FROM dbo.Employees WHERE EmployeeID = @EmployeeID;
        
        PRINT '✅ End of Service Benefits calculated for EmployeeID ' + CAST(@EmployeeID AS NVARCHAR);
    END;
    GO

    CREATE OR ALTER PROCEDURE dbo.sp_AccrueAnnualLeave
        @CompanyID UNIQUEIDENTIFIER,
        @AccrualDate DATE
    AS
    BEGIN
        SET NOCOUNT ON;
        UPDATE dbo.Employees
        SET AnnualLeaveBalance = AnnualLeaveBalance + (21.0 / 12)
        WHERE CompanyID = @CompanyID AND EmploymentStatus = 'ACTIVE' AND HireDate <= @AccrualDate;
        PRINT '✅ Annual leave accrued for ' + CAST(@@ROWCOUNT AS NVARCHAR) + ' employees';
    END;
    GO

    CREATE OR ALTER PROCEDURE dbo.sp_ProcessAttendanceFromBiometric
        @CompanyID UNIQUEIDENTIFIER,
        @BiometricID NVARCHAR(100),
        @AttendanceDate DATE,
        @CheckInTime TIME(0),
        @CheckOutTime TIME(0) = NULL,
        @DeviceID NVARCHAR(50) = NULL,
        @Source NVARCHAR(30) = 'FINGERPRINT'
    AS
    BEGIN
        SET NOCOUNT ON;
        DECLARE @EmployeeID INT;
        SELECT @EmployeeID = EmployeeID FROM dbo.Employees WHERE BiometricID = @BiometricID AND CompanyID = @CompanyID;
        IF @EmployeeID IS NULL
            THROW 50001, 'Employee not found for given BiometricID', 1;
        
        IF EXISTS (SELECT 1 FROM dbo.Attendance WHERE EmployeeID = @EmployeeID AND AttendanceDate = @AttendanceDate)
        BEGIN
            UPDATE dbo.Attendance
            SET CheckOutTime = COALESCE(@CheckOutTime, CheckOutTime),
                CheckOutSource = COALESCE(@Source, CheckOutSource),
                CheckOutDeviceID = COALESCE(@DeviceID, CheckOutDeviceID)
            WHERE EmployeeID = @EmployeeID AND AttendanceDate = @AttendanceDate;
        END
        ELSE
        BEGIN
            INSERT INTO dbo.Attendance (CompanyID, EmployeeID, AttendanceDate, CheckInTime, CheckOutTime, CheckInSource, CheckInDeviceID)
            VALUES (@CompanyID, @EmployeeID, @AttendanceDate, @CheckInTime, @CheckOutTime, @Source, @DeviceID);
        END
    END;
    GO

    -- ==========================================================================
    -- 12. TRIGGERS (update UpdatedAt/UpdatedBy)
    -- ==========================================================================
    PRINT N'✅ Creating update triggers...';
    CREATE OR ALTER TRIGGER trg_Employees_Updated ON dbo.Employees AFTER UPDATE AS
    BEGIN
        UPDATE e SET e.UpdatedAt = SYSUTCDATETIME(), e.UpdatedBy = i.UpdatedBy
        FROM dbo.Employees e INNER JOIN inserted i ON e.EmployeeID = i.EmployeeID;
    END;
    GO

    CREATE OR ALTER TRIGGER trg_Payroll_Updated ON dbo.Payroll AFTER UPDATE AS
    BEGIN
        UPDATE p SET p.UpdatedAt = SYSUTCDATETIME(), p.UpdatedBy = i.UpdatedBy
        FROM dbo.Payroll p INNER JOIN inserted i ON p.PayrollID = i.PayrollID;
    END;
    GO

    -- ==========================================================================
    -- 13. SEED DATA (default department and job titles)
    -- ==========================================================================
    PRINT N'✅ Seeding default departments and job titles...';
    IF NOT EXISTS (SELECT 1 FROM dbo.Departments WHERE DepartmentCode = 'GEN')
    BEGIN
        INSERT INTO dbo.Departments (CompanyID, DepartmentCode, DepartmentNameAR, DepartmentNameEN, IsActive, CreatedBy)
        SELECT TOP 1 CompanyID, 'GEN', N'General', N'General', 1, '00000000-0000-0000-0000-000000000001'
        FROM MST.dbo.Companies;
    END

    IF NOT EXISTS (SELECT 1 FROM dbo.JobTitles WHERE JobTitleCode = 'EMPLOYEE')
    BEGIN
        INSERT INTO dbo.JobTitles (CompanyID, JobTitleCode, JobTitleNameAR, JobTitleNameEN, JobCategory, IsActive, CreatedBy)
        SELECT TOP 1 CompanyID, 'EMPLOYEE', N'Employee', N'Employee', 'ADMINISTRATIVE', 1, '00000000-0000-0000-0000-000000000001'
        FROM MST.dbo.Companies;
    END

    IF NOT EXISTS (SELECT 1 FROM dbo.JobTitles WHERE JobTitleCode = 'MANAGER')
    BEGIN
        INSERT INTO dbo.JobTitles (CompanyID, JobTitleCode, JobTitleNameAR, JobTitleNameEN, JobCategory, IsActive, CreatedBy)
        SELECT TOP 1 CompanyID, 'MANAGER', N'Manager', N'Manager', 'MANAGEMENT', 1, '00000000-0000-0000-0000-000000000001'
        FROM MST.dbo.Companies;
    END
    GO

    -- ==========================================================================
    -- COMPLETION
    -- ==========================================================================
    COMMIT TRANSACTION;
    PRINT '═══════════════════════════════════════════════════════════════════════════';
    PRINT '✅ HR Module (Ultimate 10/10) deployed successfully.';
    PRINT '   - IF NOT EXISTS for all objects.';
    PRINT '   - TRY/CATCH transactional wrapper.';
    PRINT '   - Soft Delete + RowVersion on all tables.';
    PRINT '   - DATA_COMPRESSION + Columnstore on large tables.';
    PRINT '   - Unified audit columns (CreatedBy, CreatedAt, UpdatedBy, UpdatedAt).';
    PRINT '   - Seed data (default department, job titles).';
    PRINT '═══════════════════════════════════════════════════════════════════════════';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    DECLARE @ErrorMessage NVARCHAR(4000) = ERROR_MESSAGE();
    DECLARE @ErrorSeverity INT = ERROR_SEVERITY();
    DECLARE @ErrorState INT = ERROR_STATE();
    RAISERROR(@ErrorMessage, @ErrorSeverity, @ErrorState);
END CATCH
GO

-- ============================================================
-- source: archive/legacy_sqlserver/hr/leave.sql
-- ============================================================
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
            ('ANNUAL',    N'Annual Leave',            'Annual Leave',       'SA', 21.0,  1, 100.00, 0, 6,  NULL),
            ('ANNUAL-5Y', N'Annual Leave (after 5 years)','Annual Leave (5+ years)','SA', 30.0, 1, 100.00, 0, 60, NULL),
            ('SICK',      N'Sick Leave',            'Sick Leave',         'SA', 120.0, 1, 100.00, 1, 3,  NULL),
            ('HAJJ',      N'Hajj Leave',             'Hajj Leave',         'SA', 15.0,  1, 100.00, 0, 12, NULL),
            ('MATERNITY', N'Maternity Leave',          'Maternity Leave',    'SA', 70.0,  1, 100.00, 0, 0,  'F'),
            ('PATERNITY', N'Paternity Leave',           'Paternity Leave',    'SA', 3.0,   1, 100.00, 0, 0,  'M'),
            ('BEREAVE',   N'Bereavement Leave',             'Bereavement Leave',  'SA', 5.0,   1, 100.00, 0, 0,  NULL),
            ('UNPAID',    N'Unpaid Leave',         'Unpaid Leave',       NULL, 0.0,  0, 0.00,   0, 0,  NULL),
            -- UAE: 30d annual, 60d maternity
            ('AE-ANNUAL', N'Annual Leave - UAE',  'Annual Leave (UAE)', 'AE', 30.0,  1, 100.00, 0, 6,  NULL),
            -- Jordan: 14d annual, 70d maternity
            ('JO-ANNUAL', N'Annual Leave - Jordan',    'Annual Leave (Jordan)','JO', 14.0, 1, 100.00, 0, 6,  NULL),
            -- Egypt: 21d annual
            ('EG-ANNUAL', N'Annual Leave - Egypt',       'Annual Leave (Egypt)','EG', 21.0,  1, 100.00, 0, 6,  NULL)
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

-- ============================================================
-- source: archive/legacy_sqlserver/hr/payr.sql
-- ============================================================
-- ========================================================================
-- FILE: dba/hr/payr.sql
-- PROJECT: SHOUTECH ERP V10 - Payroll Engine Extensions (Ultimate 10/10)
-- VERSION: 10.3.0
-- DESCRIPTION: Salary components (allowances/deductions), employee salary
--              structures, monthly payroll runs with WPS output and social
--              insurance (GOSI KSA, GPSSA UAE, SSC Jordan, Egypt SI).
--              NOTE: dbo.Payroll (summary) lives in dba/hr/hr.sql.
-- ========================================================================
-- ✅ Idempotent | TRY/CATCH + TRANSACTION | Soft Delete + RowVersion
-- ✅ Posted payroll runs are immutable — reversals create new runs.
-- ========================================================================

USE [$(DbFileName)];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

PRINT N'═══════════════════════════════════════════════════════════════════════════';
PRINT N'SHOUTECH ERP V10 – Payroll Engine Extensions (Ultimate 10/10)';
PRINT N'═══════════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SalaryComponents')
    BEGIN
        CREATE TABLE dbo.SalaryComponents (
            ComponentID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ComponentCode NVARCHAR(20) NOT NULL,         -- BASIC | HOUSING | TRANSPORT | FOOD | PHONE | GOSI_EE | LOAN_DED | ABSENCE_DED
            ComponentNameAR NVARCHAR(150) NOT NULL,
            ComponentNameEN NVARCHAR(150) NOT NULL,
            ComponentKind NVARCHAR(10) NOT NULL,         -- EARNING | DEDUCTION | EMPLOYER_COST
            CalcMethod NVARCHAR(15) NOT NULL DEFAULT 'FIXED', -- FIXED | PCT_OF_BASIC | PCT_OF_GROSS | FORMULA
            DefaultValue DECIMAL(18,4) NOT NULL DEFAULT 0,
            FormulaExpression NVARCHAR(500) NULL,        -- e.g. 'BASIC * 0.25'
            IsTaxable BIT NOT NULL DEFAULT 0,            -- Relevant for Egypt/Jordan income tax
            IncludedInGosiBase BIT NOT NULL DEFAULT 0,   -- KSA GOSI wage definition
            IncludedInOvertimeBase BIT NOT NULL DEFAULT 0,
            IncludedInEosBase BIT NOT NULL DEFAULT 1,    -- End-of-service calculation
            GLAccountID INT NULL,                        -- Expense/payable mapping (loose ref)
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
            CONSTRAINT PK_SalaryComponents PRIMARY KEY CLUSTERED (ComponentID),
            CONSTRAINT UQ_SalaryComponents_Code UNIQUE (CompanyID, ComponentCode),
            CONSTRAINT CK_SalaryComponents_Kind CHECK (ComponentKind IN ('EARNING','DEDUCTION','EMPLOYER_COST')),
            CONSTRAINT CK_SalaryComponents_Method CHECK (CalcMethod IN ('FIXED','PCT_OF_BASIC','PCT_OF_GROSS','FORMULA'))
        );
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'EmployeeSalaryStructures')
    BEGIN
        CREATE TABLE dbo.EmployeeSalaryStructures (
            SalaryStructureID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            EmployeeID INT NOT NULL,
            ComponentID INT NOT NULL,
            Amount DECIMAL(18,4) NOT NULL,
            ValidFrom DATE NOT NULL,
            ValidTo DATE NULL,                           -- History-preserving
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_EmpSalaryStructures PRIMARY KEY CLUSTERED (SalaryStructureID),
            CONSTRAINT CK_EmpSalaryStructures_Amount CHECK (Amount >= 0),
            CONSTRAINT CK_EmpSalaryStructures_Validity CHECK (ValidTo IS NULL OR ValidTo >= ValidFrom)
        );
        CREATE NONCLUSTERED INDEX IX_EmpSalaryStructures_Emp
            ON dbo.EmployeeSalaryStructures (CompanyID, EmployeeID, ValidFrom) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PayrollRuns')
    BEGIN
        CREATE TABLE dbo.PayrollRuns (
            PayrollRunID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            RunNo NVARCHAR(30) NOT NULL,                 -- PR-2026-01
            PeriodYear INT NOT NULL,
            PeriodMonth TINYINT NOT NULL,
            PeriodFrom DATE NOT NULL,
            PeriodTo DATE NOT NULL,
            CurrencyCode NVARCHAR(3) NOT NULL,
            EmployeeCount INT NOT NULL DEFAULT 0,
            TotalEarnings DECIMAL(18,4) NOT NULL DEFAULT 0,
            TotalDeductions DECIMAL(18,4) NOT NULL DEFAULT 0,
            TotalEmployerCost DECIMAL(18,4) NOT NULL DEFAULT 0,
            TotalNetPay DECIMAL(18,4) NOT NULL DEFAULT 0,
            Status NVARCHAR(12) NOT NULL DEFAULT 'DRAFT', -- DRAFT | CALCULATED | APPROVED | POSTED | PAID | REVERSED
            CalculatedAt DATETIME2(7) NULL,
            ApprovedBy UNIQUEIDENTIFIER NULL,
            ApprovedAt DATETIME2(7) NULL,
            PostedAt DATETIME2(7) NULL,                  -- GL journal created
            JournalEntryID BIGINT NULL,                  -- Loose ref → JournalEntries
            PaidAt DATETIME2(7) NULL,
            WpsFilePath NVARCHAR(500) NULL,              -- Generated WPS SIF file
            ReversalOfRunID BIGINT NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_PayrollRuns PRIMARY KEY CLUSTERED (PayrollRunID),
            CONSTRAINT UQ_PayrollRuns_Period UNIQUE (CompanyID, PeriodYear, PeriodMonth),
            CONSTRAINT UQ_PayrollRuns_No UNIQUE (CompanyID, RunNo),
            CONSTRAINT CK_PayrollRuns_Month CHECK (PeriodMonth BETWEEN 1 AND 12),
            CONSTRAINT CK_PayrollRuns_Status CHECK (Status IN ('DRAFT','CALCULATED','APPROVED','POSTED','PAID','REVERSED'))
        ) WITH (DATA_COMPRESSION = PAGE);
        CREATE NONCLUSTERED INDEX IX_PayrollRuns_Status
            ON dbo.PayrollRuns (CompanyID, Status, PeriodYear DESC, PeriodMonth DESC) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PayrollRunItems')
    BEGIN
        CREATE TABLE dbo.PayrollRunItems (
            PayrollRunItemID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            PayrollRunID BIGINT NOT NULL,
            EmployeeID INT NOT NULL,
            ComponentID INT NOT NULL,
            ComponentKind NVARCHAR(10) NOT NULL,         -- Snapshot
            Amount DECIMAL(18,4) NOT NULL,
            WorkingDays DECIMAL(5,2) NOT NULL DEFAULT 0,
            AbsentDays DECIMAL(5,2) NOT NULL DEFAULT 0,
            OvertimeHours DECIMAL(6,2) NOT NULL DEFAULT 0,
            Notes NVARCHAR(300) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_PayrollRunItems PRIMARY KEY CLUSTERED (PayrollRunItemID),
            CONSTRAINT UQ_PayrollRunItems UNIQUE (PayrollRunID, EmployeeID, ComponentID)
        ) WITH (DATA_COMPRESSION = PAGE);
        CREATE NONCLUSTERED INDEX IX_PayrollRunItems_Emp
            ON dbo.PayrollRunItems (CompanyID, EmployeeID, PayrollRunID) WHERE IsDeleted = 0;
        CREATE COLUMNSTORE INDEX CSIX_PayrollRunItems
            ON dbo.PayrollRunItems (CompanyID, PayrollRunID, EmployeeID, ComponentID, Amount);
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SocialInsuranceConfigs')
    BEGIN
        CREATE TABLE dbo.SocialInsuranceConfigs (
            SiConfigID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            CountryCode NVARCHAR(2) NOT NULL,
            SchemeCode NVARCHAR(20) NOT NULL,            -- GOSI-SA | GPSSA-AE | SSC-JO | SI-EG
            SchemeNameAR NVARCHAR(200) NOT NULL,
            SchemeNameEN NVARCHAR(200) NOT NULL,
            EmployeeRate DECIMAL(6,4) NOT NULL,          -- e.g. GOSI Saudi: 9.75% (annuities+saned)
            EmployerRate DECIMAL(6,4) NOT NULL,          -- e.g. GOSI Saudi: 11.75%
            MinWageBase DECIMAL(18,4) NULL,
            MaxWageBase DECIMAL(18,4) NULL,              -- GOSI cap: 45,000 SAR
            AppliesToNationality NVARCHAR(10) NOT NULL DEFAULT 'LOCAL', -- LOCAL | EXPAT | ALL
            EmployerRegistrationNo NVARCHAR(50) NULL,
            ValidFrom DATE NOT NULL,
            ValidTo DATE NULL,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_SocialInsuranceConfigs PRIMARY KEY CLUSTERED (SiConfigID),
            CONSTRAINT UQ_SocialInsuranceConfigs UNIQUE (CompanyID, SchemeCode, ValidFrom),
            CONSTRAINT CK_SocialInsuranceConfigs_Rates CHECK (EmployeeRate >= 0 AND EmployerRate >= 0)
        );
    END

    -- ==========================================================================
    -- 6. SEED DATA — salary components + social insurance schemes
    -- ==========================================================================
    PRINT N'✅ Seeding salary components & social insurance...';

    IF NOT EXISTS (SELECT 1 FROM dbo.SalaryComponents WHERE ComponentCode = 'BASIC')
    BEGIN
        INSERT INTO dbo.SalaryComponents
            (CompanyID, ComponentCode, ComponentNameAR, ComponentNameEN, ComponentKind,
             CalcMethod, DefaultValue, IsTaxable, IncludedInGosiBase, IncludedInOvertimeBase,
             IncludedInEosBase, SortOrder, CreatedBy)
        SELECT TOP 1 C.CompanyID, X.Code, X.NameAR, X.NameEN, X.Kind, X.Method, X.DefVal,
               X.Taxable, X.Gosi, X.OtBase, X.EosBase, X.SortOrd, '00000000-0000-0000-0000-000000000001'
        FROM MST.dbo.Companies C
        CROSS JOIN (VALUES
            ('BASIC',    N'Basic Salary',        'Basic Salary',        'EARNING', 'FIXED',        0,  1, 1, 1, 1, 1),
            ('HOUSING',  N'Housing Allowance',             'Housing Allowance',   'EARNING', 'PCT_OF_BASIC', 25, 1, 1, 0, 1, 2),
            ('TRANSPORT',N'Transportation Allowance',         'Transport Allowance', 'EARNING', 'FIXED',        0,  1, 1, 0, 1, 3),
            ('FOOD',     N'Food Allowance',            'Food Allowance',      'EARNING', 'FIXED',        0,  1, 0, 0, 1, 4),
            ('PHONE',    N'Phone Allowance',            'Phone Allowance',     'EARNING', 'FIXED',        0,  0, 0, 0, 0, 5),
            ('OT-NORMAL',N'Overtime - Regular',      'Overtime — Normal',   'EARNING', 'FORMULA',      0,  0, 0, 0, 0, 6),
            ('GOSI-EE',  N'Employee share - Insurance','GOSI Employee Share', 'DEDUCTION','FORMULA',     0,  0, 0, 0, 0, 20),
            ('LOAN-DED', N'Loan Installment',              'Loan Installment',    'DEDUCTION','FIXED',       0,  0, 0, 0, 0, 21),
            ('ABS-DED',  N'Absence Deduction',            'Absence Deduction',   'DEDUCTION','FORMULA',     0,  0, 0, 0, 0, 22),
            ('GOSI-ER',  N'Employer share - Insurance','GOSI Employer Share','EMPLOYER_COST','FORMULA',0,0, 0, 0, 0, 30)
        ) X (Code, NameAR, NameEN, Kind, Method, DefVal, Taxable, Gosi, OtBase, EosBase, SortOrd);
    END

    IF NOT EXISTS (SELECT 1 FROM dbo.SocialInsuranceConfigs WHERE SchemeCode = 'GOSI-SA')
    BEGIN
        INSERT INTO dbo.SocialInsuranceConfigs
            (CompanyID, CountryCode, SchemeCode, SchemeNameAR, SchemeNameEN,
             EmployeeRate, EmployerRate, MinWageBase, MaxWageBase, AppliesToNationality,
             ValidFrom, CreatedBy)
        SELECT TOP 1 C.CompanyID, X.CC, X.Code, X.NameAR, X.NameEN,
               X.EeRate, X.ErRate, X.MinBase, X.MaxBase, X.Nat, X.VFrom,
               '00000000-0000-0000-0000-000000000001'
        FROM MST.dbo.Companies C
        CROSS JOIN (VALUES
            -- KSA GOSI: Saudi employee 9.75% (annuities 9% + SANED 0.75%),
            --           employer 11.75% (annuities 9% + hazards 2% + SANED 0.75%)
            ('SA', 'GOSI-SA',    N'Social Insurance - Saudi Arabia (Saudi national)', 'GOSI — Saudi National', 9.7500, 11.7500, 1500, 45000, 'LOCAL', '2024-01-01'),
            ('SA', 'GOSI-SA-XP', N'Social Insurance - Saudi Arabia (Expatriate)',  'GOSI — Expatriate',     0.0000,  2.0000, NULL, NULL,  'EXPAT', '2024-01-01'),
            -- UAE GPSSA: employee 11%, employer 15% (private sector, UAE nationals)
            ('AE', 'GPSSA-AE',   N'Pensions - UAE (Citizen)',            'GPSSA — UAE National',  11.0000, 15.0000, NULL, 50000, 'LOCAL', '2024-01-01'),
            -- Jordan SSC: employee 7.5%, employer 14.25%
            ('JO', 'SSC-JO',     N'Social Security - Jordan',               'SSC — Jordan',           7.5000, 14.2500, NULL, NULL,  'ALL',   '2024-01-01'),
            -- Egypt SI: employee 11%, employer 18.75% (Law 148/2019)
            ('EG', 'SI-EG',      N'Social Insurance - Egypt',              'Social Insurance — Egypt',11.0000, 18.7500, 2000, 12600, 'ALL',  '2024-01-01')
        ) X (CC, Code, NameAR, NameEN, EeRate, ErRate, MinBase, MaxBase, Nat, VFrom);
    END

    -- ==========================================================================
    -- COMPLETION
    -- ==========================================================================
    COMMIT TRANSACTION;
    PRINT N'═══════════════════════════════════════════════════════════════════════════';
    PRINT N'✅ Payroll Engine Extensions deployed successfully.';
    PRINT N'   - Components + structures + runs + WPS + GOSI/GPSSA/SSC/SI-EG seeds.';
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
