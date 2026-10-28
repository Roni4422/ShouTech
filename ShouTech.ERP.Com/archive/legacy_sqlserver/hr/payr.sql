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
            ('BASIC',    N'الراتب الأساسي',        'Basic Salary',        'EARNING', 'FIXED',        0,  1, 1, 1, 1, 1),
            ('HOUSING',  N'بدل السكن',             'Housing Allowance',   'EARNING', 'PCT_OF_BASIC', 25, 1, 1, 0, 1, 2),
            ('TRANSPORT',N'بدل المواصلات',         'Transport Allowance', 'EARNING', 'FIXED',        0,  1, 1, 0, 1, 3),
            ('FOOD',     N'بدل الطعام',            'Food Allowance',      'EARNING', 'FIXED',        0,  1, 0, 0, 1, 4),
            ('PHONE',    N'بدل الهاتف',            'Phone Allowance',     'EARNING', 'FIXED',        0,  0, 0, 0, 0, 5),
            ('OT-NORMAL',N'عمل إضافي — عادي',      'Overtime — Normal',   'EARNING', 'FORMULA',      0,  0, 0, 0, 0, 6),
            ('GOSI-EE',  N'حصة الموظف — التأمينات','GOSI Employee Share', 'DEDUCTION','FORMULA',     0,  0, 0, 0, 0, 20),
            ('LOAN-DED', N'قسط سلفة',              'Loan Installment',    'DEDUCTION','FIXED',       0,  0, 0, 0, 0, 21),
            ('ABS-DED',  N'خصم الغياب',            'Absence Deduction',   'DEDUCTION','FORMULA',     0,  0, 0, 0, 0, 22),
            ('GOSI-ER',  N'حصة صاحب العمل — التأمينات','GOSI Employer Share','EMPLOYER_COST','FORMULA',0,0, 0, 0, 0, 30)
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
            ('SA', 'GOSI-SA',    N'التأمينات الاجتماعية — السعودية (سعودي)', 'GOSI — Saudi National', 9.7500, 11.7500, 1500, 45000, 'LOCAL', '2024-01-01'),
            ('SA', 'GOSI-SA-XP', N'التأمينات الاجتماعية — السعودية (وافد)',  'GOSI — Expatriate',     0.0000,  2.0000, NULL, NULL,  'EXPAT', '2024-01-01'),
            -- UAE GPSSA: employee 11%, employer 15% (private sector, UAE nationals)
            ('AE', 'GPSSA-AE',   N'المعاشات — الإمارات (مواطن)',            'GPSSA — UAE National',  11.0000, 15.0000, NULL, 50000, 'LOCAL', '2024-01-01'),
            -- Jordan SSC: employee 7.5%, employer 14.25%
            ('JO', 'SSC-JO',     N'الضمان الاجتماعي — الأردن',               'SSC — Jordan',           7.5000, 14.2500, NULL, NULL,  'ALL',   '2024-01-01'),
            -- Egypt SI: employee 11%, employer 18.75% (Law 148/2019)
            ('EG', 'SI-EG',      N'التأمينات الاجتماعية — مصر',              'Social Insurance — Egypt',11.0000, 18.7500, 2000, 12600, 'ALL',  '2024-01-01')
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
