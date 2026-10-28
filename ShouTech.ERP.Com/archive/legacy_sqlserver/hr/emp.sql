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
