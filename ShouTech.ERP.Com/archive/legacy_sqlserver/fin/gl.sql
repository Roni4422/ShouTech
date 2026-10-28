-- ═════════════════════════════════════════════════════════════════════════════════
-- FILE: dba/fin/gl.sql
-- SYSTEM: ShouTech ERP Enterprise Core
-- MODULE: General Ledger, Chart of Accounts & Double-Entry Accounting Engine
-- GRADE: Enterprise Production Standard (Tier-1)
-- ═════════════════════════════════════════════════════════════════════════════════

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'fin')
BEGIN
    EXEC('CREATE SCHEMA [fin] AUTHORIZATION [dbo];');
END;
GO

RAISERROR(N'🚀 [ENTERPRISE BUILD] Building Financial & General Ledger Core (fin)...', 0, 1) WITH NOWAIT;

BEGIN TRANSACTION;

BEGIN TRY

    IF OBJECT_ID(N'fin.FiscalYears', N'U') IS NULL
    BEGIN
        CREATE TABLE fin.FiscalYears (
            FiscalYearID        INT IDENTITY(1,1) NOT NULL,
            TenantID            INT NOT NULL DEFAULT 1,
            CompanyID           INT NOT NULL DEFAULT 1,
            YearNumber          INT NOT NULL,
            StartDate           DATE NOT NULL,
            EndDate             DATE NOT NULL,
            IsClosed            BIT NOT NULL DEFAULT 0,
            ClosedAt            DATETIME2(7) NULL,

            CONSTRAINT PK_fin_FiscalYears PRIMARY KEY CLUSTERED (FiscalYearID),
            CONSTRAINT UQ_fin_FiscalYears UNIQUE (TenantID, CompanyID, YearNumber)
        );
    END;

    IF OBJECT_ID(N'fin.ChartOfAccounts', N'U') IS NULL
    BEGIN
        CREATE TABLE fin.ChartOfAccounts (
            AccountID           INT IDENTITY(1,1) NOT NULL,
            TenantID            INT NOT NULL DEFAULT 1,
            CompanyID           INT NOT NULL DEFAULT 1,
            AccountCode         VARCHAR(50) NOT NULL,
            AccountNameAR       NVARCHAR(200) NOT NULL,
            AccountNameEN       VARCHAR(200) NOT NULL,
            ParentAccountID     INT NULL,
            AccountLevel        TINYINT NOT NULL DEFAULT 1,
            AccountType         VARCHAR(20) NOT NULL, -- ASSET, LIABILITY, EQUITY, REVENUE, EXPENSE
            NormalBalance       CHAR(1) NOT NULL,     -- 'D' (Debit) or 'C' (Credit)
            IsPostingAccount    BIT NOT NULL DEFAULT 1, -- 1: حقيقي يقبل القيود, 0: تجميعي (Parent)
            IsActive            BIT NOT NULL DEFAULT 1,
            
            -- Audit & Soft Delete
            IsDeleted           BIT NOT NULL DEFAULT 0,
            CreatedBy           INT NULL,
            CreatedAt           DATETIME2(7) NOT NULL DEFAULT SYSDATETIME(),
            UpdatedBy           INT NULL,
            UpdatedAt           DATETIME2(7) NULL,
            RowVersion          ROWVERSION NOT NULL,

            CONSTRAINT PK_fin_ChartOfAccounts PRIMARY KEY CLUSTERED (AccountID),
            CONSTRAINT UQ_fin_ChartOfAccounts_Code UNIQUE (TenantID, CompanyID, AccountCode),
            CONSTRAINT FK_fin_ChartOfAccounts_Parent FOREIGN KEY (ParentAccountID) REFERENCES fin.ChartOfAccounts(AccountID)
        );

        CREATE NONCLUSTERED INDEX IX_fin_ChartOfAccounts_Hierarchy 
        ON fin.ChartOfAccounts (TenantID, CompanyID, ParentAccountID) 
        WHERE IsDeleted = 0;
    END;

    IF OBJECT_ID(N'fin.JournalHeaders', N'U') IS NULL
    BEGIN
        CREATE TABLE fin.JournalHeaders (
            JournalHeaderID     BIGINT IDENTITY(1,1) NOT NULL,
            TenantID            INT NOT NULL DEFAULT 1,
            CompanyID           INT NOT NULL DEFAULT 1,
            JournalNo           VARCHAR(100) NOT NULL,   -- مولد عبر sys.sp_GetNextSequenceValue
            FiscalYear          INT NOT NULL,
            PostingDate         DATE NOT NULL,
            DocumentDate        DATE NOT NULL DEFAULT CAST(SYSDATETIME() AS DATE),
            ReferenceNo         NVARCHAR(100) NULL,
            Narrative           NVARCHAR(500) NOT NULL,
            TotalDebit          DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
            TotalCredit         DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
            Status              VARCHAR(20) NOT NULL DEFAULT 'DRAFT', -- DRAFT, POSTED, VOIDED
            PostedBy            INT NULL,
            PostedAt            DATETIME2(7) NULL,
            
            -- Audit & Soft Delete
            IsDeleted           BIT NOT NULL DEFAULT 0,
            CreatedBy           INT NOT NULL,
            CreatedAt           DATETIME2(7) NOT NULL DEFAULT SYSDATETIME(),
            RowVersion          ROWVERSION NOT NULL,

            CONSTRAINT PK_fin_JournalHeaders PRIMARY KEY CLUSTERED (JournalHeaderID),
            CONSTRAINT UQ_fin_JournalHeaders_No UNIQUE (TenantID, CompanyID, JournalNo),
            CONSTRAINT CK_fin_JournalHeaders_Balance CHECK (TotalDebit = TotalCredit) -- حارس التوازن المالي الصارم
        );
    END;

    IF OBJECT_ID(N'fin.JournalLines', N'U') IS NULL
    BEGIN
        CREATE TABLE fin.JournalLines (
            JournalLineID       BIGINT IDENTITY(1,1) NOT NULL,
            JournalHeaderID     BIGINT NOT NULL,
            LineNumber          INT NOT NULL,
            AccountID           INT NOT NULL,
            Debit               DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
            Credit              DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
            LineDescription     NVARCHAR(300) NULL,
            CostCenterID        INT NULL,

            CONSTRAINT PK_fin_JournalLines PRIMARY KEY CLUSTERED (JournalLineID),
            CONSTRAINT FK_fin_JournalLines_Header FOREIGN KEY (JournalHeaderID) REFERENCES fin.JournalHeaders(JournalHeaderID) ON DELETE CASCADE,
            CONSTRAINT FK_fin_JournalLines_Account FOREIGN KEY (AccountID) REFERENCES fin.ChartOfAccounts(AccountID),
            CONSTRAINT CK_fin_JournalLines_Positive CHECK (Debit >= 0 AND Credit >= 0)
        );

        CREATE NONCLUSTERED INDEX IX_fin_JournalLines_Header 
        ON fin.JournalLines (JournalHeaderID) 
        INCLUDE (AccountID, Debit, Credit);
    END;

    COMMIT TRANSACTION;
    RAISERROR(N'✅ [SUCCESS] General Ledger architecture created successfully (fin.*).', 0, 1) WITH NOWAIT;

END TRY
BEGIN CATCH
    IF (XACT_STATE()) <> 0 ROLLBACK TRANSACTION;
    DECLARE @Err NVARCHAR(4000) = ERROR_MESSAGE();
    RAISERROR(N'❌ [FATAL ERROR] gl.sql failed: %s', 16, 1, @Err);
END CATCH;
GO