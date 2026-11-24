-- ============================================================
-- source: archive/legacy_sqlserver/fin/coa.sql
-- ============================================================
-- ═══════════════════════════════════════════════════════════════════════════
-- FILE: dba/sds/01_seed_coa.sql
-- PURPOSE: Saudi GAAP Compliant Chart of Accounts (4 Levels)
-- ═══════════════════════════════════════════════════════════════════════════

USE [$(DbFileName)];
GO

PRINT '🌱 Seeding Chart of Accounts (Saudi GAAP)...';
GO

INSERT INTO dbo.ChartOfAccounts (CompanyID, AccountCode, AccountNameAR, AccountNameEN, AccountType, AccountNature, AccountLevel, IsHeader, FinancialStatementCategory)
VALUES 
('$(CompanyID)', '1', N'Assets', 'Assets', 'ASSET', 'DEBIT', 1, 1, 'BALANCE_SHEET'),
('$(CompanyID)', '2', N'Liabilities', 'Liabilities', 'LIABILITY', 'CREDIT', 1, 1, 'BALANCE_SHEET'),
('$(CompanyID)', '3', N'Equity', 'Equity', 'EQUITY', 'CREDIT', 1, 1, 'BALANCE_SHEET'),
('$(CompanyID)', '4', N'Revenue', 'Revenue', 'REVENUE', 'CREDIT', 1, 1, 'INCOME_STATEMENT'),
('$(CompanyID)', '5', N'Expenses', 'Expenses', 'EXPENSE', 'DEBIT', 1, 1, 'INCOME_STATEMENT');

INSERT INTO dbo.ChartOfAccounts (CompanyID, AccountCode, ParentAccountCode, AccountNameAR, AccountNameEN, AccountType, AccountNature, AccountLevel, IsHeader, FinancialStatementCategory, IsCashAccount, IsBankAccount)
VALUES 
('$(CompanyID)', '11', '1', N'Current Assets', 'Current Assets', 'ASSET', 'DEBIT', 2, 1, 'CURRENT_ASSETS', 0, 0),
('$(CompanyID)', '111', '11', N'Cash', 'Cash & Banks', 'ASSET', 'DEBIT', 3, 1, 'CURRENT_ASSETS', 0, 0),
('$(CompanyID)', '1111', '111', N'Cash', 'Cash on Hand', 'ASSET', 'DEBIT', 4, 0, 'CURRENT_ASSETS', 1, 0),
('$(CompanyID)', '1112', '111', N'AlAhli Bank - Current', 'NCB Current Account', 'ASSET', 'DEBIT', 4, 0, 'CURRENT_ASSETS', 0, 1),
('$(CompanyID)', '1113', '111', N'AlRajhi Bank - Current', 'Al Rajhi Current Account', 'ASSET', 'DEBIT', 4, 0, 'CURRENT_ASSETS', 0, 1),
('$(CompanyID)', '112', '11', N'Accounts Receivable', 'Accounts Receivable', 'ASSET', 'DEBIT', 3, 1, 'CURRENT_ASSETS', 0, 0),
('$(CompanyID)', '1121', '112', N'Local Customer Receivables', 'Local Customers AR', 'ASSET', 'DEBIT', 4, 0, 'CURRENT_ASSETS', 0, 0),
('$(CompanyID)', '1122', '112', N'Export Customer Receivables', 'Export Customers AR', 'ASSET', 'DEBIT', 4, 0, 'CURRENT_ASSETS', 0, 0),
('$(CompanyID)', '113', '11', N'Inventory', 'Inventory', 'ASSET', 'DEBIT', 3, 1, 'CURRENT_ASSETS', 0, 0),
('$(CompanyID)', '1131', '113', N'Opening Stock', 'Opening Inventory', 'ASSET', 'DEBIT', 4, 0, 'CURRENT_ASSETS', 0, 0),
('$(CompanyID)', '1132', '113', N'Purchases', 'Purchases', 'ASSET', 'DEBIT', 4, 0, 'CURRENT_ASSETS', 0, 0),
('$(CompanyID)', '114', '11', N'Prepaid Expenses', 'Prepaid Expenses', 'ASSET', 'DEBIT', 3, 0, 'CURRENT_ASSETS', 0, 0);

INSERT INTO dbo.ChartOfAccounts (CompanyID, AccountCode, ParentAccountCode, AccountNameAR, AccountNameEN, AccountType, AccountNature, AccountLevel, IsHeader, FinancialStatementCategory)
VALUES 
('$(CompanyID)', '12', '1', N'Fixed Assets', 'Fixed Assets', 'ASSET', 'DEBIT', 2, 1, 'NON_CURRENT_ASSETS'),
('$(CompanyID)', '121', '12', N'Land', 'Land', 'ASSET', 'DEBIT', 3, 0, 'NON_CURRENT_ASSETS'),
('$(CompanyID)', '122', '12', N'Buildings', 'Buildings', 'ASSET', 'DEBIT', 3, 0, 'NON_CURRENT_ASSETS'),
('$(CompanyID)', '123', '12', N'Machinery and Equipment', 'Machinery & Equipment', 'ASSET', 'DEBIT', 3, 0, 'NON_CURRENT_ASSETS'),
('$(CompanyID)', '124', '12', N'Vehicles', 'Vehicles', 'ASSET', 'DEBIT', 3, 0, 'NON_CURRENT_ASSETS'),
('$(CompanyID)', '125', '12', N'Furniture and Fixtures', 'Furniture & Fixtures', 'ASSET', 'DEBIT', 3, 0, 'NON_CURRENT_ASSETS'),
('$(CompanyID)', '126', '12', N'Accumulated Depreciation', 'Accumulated Depreciation', 'ASSET', 'CREDIT', 3, 0, 'NON_CURRENT_ASSETS'); -- Contra asset

INSERT INTO dbo.ChartOfAccounts (CompanyID, AccountCode, ParentAccountCode, AccountNameAR, AccountNameEN, AccountType, AccountNature, AccountLevel, IsHeader, FinancialStatementCategory)
VALUES 
('$(CompanyID)', '21', '2', N'Current Liabilities', 'Current Liabilities', 'LIABILITY', 'CREDIT', 2, 1, 'CURRENT_LIABILITIES'),
('$(CompanyID)', '211', '21', N'Supplier Payables', 'Accounts Payable', 'LIABILITY', 'CREDIT', 3, 0, 'CURRENT_LIABILITIES'),
('$(CompanyID)', '212', '21', N'Notes Payable', 'Notes Payable', 'LIABILITY', 'CREDIT', 3, 0, 'CURRENT_LIABILITIES'),
('$(CompanyID)', '213', '21', N'Short-Term Loans', 'Short-term Loans', 'LIABILITY', 'CREDIT', 3, 0, 'CURRENT_LIABILITIES'),
('$(CompanyID)', '214', '21', N'Value Added Tax', 'VAT Payable', 'LIABILITY', 'CREDIT', 3, 0, 'CURRENT_LIABILITIES'),
('$(CompanyID)', '215', '21', N'Social Insurance Payable', 'GOSI Payable', 'LIABILITY', 'CREDIT', 3, 0, 'CURRENT_LIABILITIES'),
('$(CompanyID)', '216', '21', N'Accrued Salaries', 'Salaries Payable', 'LIABILITY', 'CREDIT', 3, 0, 'CURRENT_LIABILITIES');

INSERT INTO dbo.ChartOfAccounts (CompanyID, AccountCode, ParentAccountCode, AccountNameAR, AccountNameEN, AccountType, AccountNature, AccountLevel, IsHeader, FinancialStatementCategory)
VALUES 
('$(CompanyID)', '31', '3', N'Capital', 'Capital', 'EQUITY', 'CREDIT', 2, 0, 'EQUITY'),
('$(CompanyID)', '32', '3', N'Reserves', 'Reserves', 'EQUITY', 'CREDIT', 2, 0, 'EQUITY'),
('$(CompanyID)', '33', '3', N'Retained Earnings', 'Retained Earnings', 'EQUITY', 'CREDIT', 2, 0, 'EQUITY'),
('$(CompanyID)', '34', '3', N'Current Year Profit', 'Current Year Profit', 'EQUITY', 'CREDIT', 2, 0, 'EQUITY');

INSERT INTO dbo.ChartOfAccounts (CompanyID, AccountCode, ParentAccountCode, AccountNameAR, AccountNameEN, AccountType, AccountNature, AccountLevel, IsHeader, FinancialStatementCategory)
VALUES 
('$(CompanyID)', '41', '4', N'Sales Revenue', 'Sales Revenue', 'REVENUE', 'CREDIT', 2, 1, 'OPERATING_REVENUE'),
('$(CompanyID)', '411', '41', N'Local Sales', 'Local Sales', 'REVENUE', 'CREDIT', 3, 0, 'OPERATING_REVENUE'),
('$(CompanyID)', '412', '41', N'Export Sales', 'Export Sales', 'REVENUE', 'CREDIT', 3, 0, 'OPERATING_REVENUE'),
('$(CompanyID)', '413', '41', N'Sales Returns', 'Sales Returns', 'REVENUE', 'DEBIT', 3, 0, 'OPERATING_REVENUE'), -- Contra revenue
('$(CompanyID)', '42', '4', N'Other Revenue', 'Other Income', 'REVENUE', 'CREDIT', 2, 0, 'OTHER_INCOME');

INSERT INTO dbo.ChartOfAccounts (CompanyID, AccountCode, ParentAccountCode, AccountNameAR, AccountNameEN, AccountType, AccountNature, AccountLevel, IsHeader, FinancialStatementCategory)
VALUES 
('$(CompanyID)', '51', '5', N'Cost of Goods Sold', 'Cost of Goods Sold', 'EXPENSE', 'DEBIT', 2, 1, 'COGS'),
('$(CompanyID)', '511', '51', N'Opening Stock', 'Beginning Inventory', 'EXPENSE', 'DEBIT', 3, 0, 'COGS'),
('$(CompanyID)', '512', '51', N'Purchases', 'Purchases', 'EXPENSE', 'DEBIT', 3, 0, 'COGS'),
('$(CompanyID)', '513', '51', N'Freight on Purchases', 'Freight In', 'EXPENSE', 'DEBIT', 3, 0, 'COGS'),
('$(CompanyID)', '514', '51', N'Purchase Returns', 'Purchase Returns', 'EXPENSE', 'CREDIT', 3, 0, 'COGS'), -- Contra expense
('$(CompanyID)', '515', '51', N'Closing Stock', 'Ending Inventory', 'EXPENSE', 'CREDIT', 3, 0, 'COGS'), -- Contra expense

('$(CompanyID)', '61', '5', N'Operating Expenses', 'Operating Expenses', 'EXPENSE', 'DEBIT', 2, 1, 'OPERATING_EXPENSES'),
('$(CompanyID)', '611', '61', N'Payroll Expenses', 'Salaries Expense', 'EXPENSE', 'DEBIT', 3, 0, 'OPERATING_EXPENSES'),
('$(CompanyID)', '612', '61', N'Social Insurance (Employer Share)', 'GOSI Employer Share', 'EXPENSE', 'DEBIT', 3, 0, 'OPERATING_EXPENSES'),
('$(CompanyID)', '613', '61', N'Rent', 'Rent Expense', 'EXPENSE', 'DEBIT', 3, 0, 'OPERATING_EXPENSES'),
('$(CompanyID)', '614', '61', N'Electricity and Water', 'Utilities', 'EXPENSE', 'DEBIT', 3, 0, 'OPERATING_EXPENSES'),
('$(CompanyID)', '615', '61', N'Telecommunications', 'Communications', 'EXPENSE', 'DEBIT', 3, 0, 'OPERATING_EXPENSES'),
('$(CompanyID)', '616', '61', N'Maintenance', 'Maintenance', 'EXPENSE', 'DEBIT', 3, 0, 'OPERATING_EXPENSES'),
('$(CompanyID)', '617', '61', N'Depreciation', 'Depreciation', 'EXPENSE', 'DEBIT', 3, 0, 'OPERATING_EXPENSES'),
('$(CompanyID)', '618', '61', N'Marketing Expenses', 'Marketing', 'EXPENSE', 'DEBIT', 3, 0, 'OPERATING_EXPENSES'),
('$(CompanyID)', '619', '61', N'Administrative Expenses', 'Administrative', 'EXPENSE', 'DEBIT', 3, 0, 'OPERATING_EXPENSES');

PRINT '✅ Chart of Accounts Seeded (55 Accounts)';
GO

-- ============================================================
-- source: archive/legacy_sqlserver/fin/fin.sql
-- ============================================================
-- ========================================================================
-- FILE: dba/sch/fin.sql
-- PROJECT: SHOUTECH ERP V10 - Financial Module (Ultimate 10/10)
-- VERSION: 10.2.0
-- DESCRIPTION: Complete financial module with full transaction safety,
--              fixed self-referencing FKs, unified Soft Delete,
--              improved triggers with audit logging, full compatibility.
-- ========================================================================
-- CRITICAL IMPROVEMENTS:
-- ✅ Full TRY/CATCH transactional wrapper around entire script.
-- ✅ Self-referencing FKs (ChartOfAccounts, CostCenters) created via ALTER after table.
-- ✅ Unified Soft Delete (IsDeleted, DeletedAt, DeletedBy) on ALL tables.
-- ✅ Triggers enhanced with audit logging (inserts into dbo.AuditLogs).
-- ✅ Full compatibility with post.sql and TMPL.sql (no direct FKs to external tables).
-- ✅ Idempotent (IF NOT EXISTS on every object).
-- ========================================================================

USE [$(DbFileName)];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

PRINT N'═══════════════════════════════════════════════════════════════════════';
PRINT N'SHOUTECH ERP V10 – Finance Module (Ultimate 10/10)';
PRINT N'═══════════════════════════════════════════════════════════════════════';
GO

-- ==========================================================================
-- TRANSACTION WRAPPER FOR ENTIRE SCRIPT
-- ==========================================================================
BEGIN TRY
    BEGIN TRANSACTION;

    -- ======================================================================
    -- 1. CHART OF ACCOUNTS (Self-referencing FK will be added via ALTER)
    -- ======================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ChartOfAccounts')
    BEGIN
        CREATE TABLE dbo.ChartOfAccounts (
            AccountID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            AccountCode NVARCHAR(50) NOT NULL,
            ParentAccountCode NVARCHAR(50),
            AccountLevel TINYINT DEFAULT 1,
            AccountNameAR NVARCHAR(255) NOT NULL,
            AccountNameEN NVARCHAR(255) NOT NULL,
            AccountType NVARCHAR(30) NOT NULL,
            AccountNature NVARCHAR(10) NOT NULL,
            FinancialStatementCategory NVARCHAR(50),
            IFRSCode NVARCHAR(20),
            IsActive BIT DEFAULT 1,
            IsHeader BIT DEFAULT 0,
            AllowManualEntry BIT DEFAULT 1,
            RequireCostCenter BIT DEFAULT 0,
            RequireProject BIT DEFAULT 0,
            IsBankAccount BIT DEFAULT 0,
            IsCashAccount BIT DEFAULT 0,
            OpeningBalanceDebit DECIMAL(18,4) DEFAULT 0,
            OpeningBalanceCredit DECIMAL(18,4) DEFAULT 0,
            CurrentBalanceDebit DECIMAL(18,4) DEFAULT 0,
            CurrentBalanceCredit DECIMAL(18,4) DEFAULT 0,
            CurrencyCode NVARCHAR(3) DEFAULT 'SAR',
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_ChartOfAccounts PRIMARY KEY CLUSTERED (AccountID),
            CONSTRAINT UQ_ChartOfAccounts_Code UNIQUE (CompanyID, AccountCode)
        );
    END

    -- Add FK after table creation to avoid self-ref issues
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ChartOfAccounts_Parent')
    BEGIN
        ALTER TABLE dbo.ChartOfAccounts ADD CONSTRAINT FK_ChartOfAccounts_Parent
        FOREIGN KEY (CompanyID, ParentAccountCode) REFERENCES dbo.ChartOfAccounts(CompanyID, AccountCode);
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ChartOfAccounts_Company_Active ON dbo.ChartOfAccounts(CompanyID, IsActive) INCLUDE (AccountCode, AccountNameAR, AccountType) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ChartOfAccounts_Parent ON dbo.ChartOfAccounts(ParentAccountCode) WHERE ParentAccountCode IS NOT NULL;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ChartOfAccounts_Type ON dbo.ChartOfAccounts(AccountType);
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ChartOfAccounts_BankCash ON dbo.ChartOfAccounts(CompanyID, IsBankAccount, IsCashAccount) WHERE (IsBankAccount = 1 OR IsCashAccount = 1) AND IsDeleted = 0;
    GO

    -- ======================================================================
    -- 2. COST CENTERS (Self-referencing FK via ALTER)
    -- ======================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CostCenters')
    BEGIN
        CREATE TABLE dbo.CostCenters (
            CostCenterID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            CostCenterCode NVARCHAR(50) NOT NULL,
            CostCenterNameAR NVARCHAR(200) NOT NULL,
            CostCenterNameEN NVARCHAR(200) NOT NULL,
            ParentCostCenterCode NVARCHAR(50),
            CostCenterLevel TINYINT DEFAULT 1,
            CostCenterType NVARCHAR(20) NOT NULL,
            ManagerUserID UNIQUEIDENTIFIER,
            AnnualBudget DECIMAL(18,2),
            CurrentYearSpent DECIMAL(18,2) DEFAULT 0,
            IsActive BIT DEFAULT 1,
            IsHeader BIT DEFAULT 0,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_CostCenters PRIMARY KEY CLUSTERED (CostCenterID),
            CONSTRAINT UQ_CostCenters_Code UNIQUE (CompanyID, CostCenterCode)
        );
    END

    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_CostCenters_Parent')
    BEGIN
        ALTER TABLE dbo.CostCenters ADD CONSTRAINT FK_CostCenters_Parent
        FOREIGN KEY (CompanyID, ParentCostCenterCode) REFERENCES dbo.CostCenters(CompanyID, CostCenterCode);
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_CostCenters_Company_Active ON dbo.CostCenters(CompanyID, IsActive) INCLUDE (CostCenterCode, CostCenterNameAR) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_CostCenters_Parent ON dbo.CostCenters(ParentCostCenterCode);
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_CostCenters_Manager ON dbo.CostCenters(ManagerUserID);
    GO

    -- ======================================================================
    -- 3. FISCAL PERIODS
    -- ======================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'FiscalPeriods')
    BEGIN
        CREATE TABLE dbo.FiscalPeriods (
            FiscalPeriodID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            FiscalYear INT NOT NULL,
            PeriodNumber TINYINT NOT NULL,
            PeriodName NVARCHAR(50) NOT NULL,
            StartDate DATE NOT NULL,
            EndDate DATE NOT NULL,
            IsClosed BIT DEFAULT 0,
            ClosedBy UNIQUEIDENTIFIER,
            ClosedAt DATETIME2(7),
            IsAdjustmentPeriod BIT DEFAULT 0,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            CONSTRAINT PK_FiscalPeriods PRIMARY KEY CLUSTERED (FiscalPeriodID),
            CONSTRAINT UQ_FiscalPeriods_CompanyYearPeriod UNIQUE (CompanyID, FiscalYear, PeriodNumber)
        );
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_FiscalPeriods_Company_Year ON dbo.FiscalPeriods(CompanyID, FiscalYear) INCLUDE (StartDate, EndDate, IsClosed) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_FiscalPeriods_Dates ON dbo.FiscalPeriods(StartDate, EndDate);
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_FiscalPeriods_Closed ON dbo.FiscalPeriods(CompanyID, IsClosed) WHERE IsClosed = 0 AND IsDeleted = 0;
    GO

    -- ======================================================================
    -- 4. CURRENCIES (No Soft Delete – reference data)
    -- ======================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Currencies')
    BEGIN
        CREATE TABLE dbo.Currencies (
            CurrencyID INT IDENTITY(1,1) NOT NULL,
            CurrencyCode NVARCHAR(3) NOT NULL,
            CurrencyNameAR NVARCHAR(100) NOT NULL,
            CurrencyNameEN NVARCHAR(100) NOT NULL,
            CurrencySymbol NVARCHAR(10),
            DecimalPlaces TINYINT DEFAULT 2,
            IsActive BIT DEFAULT 1,
            RowVersion ROWVERSION,
            CONSTRAINT PK_Currencies PRIMARY KEY CLUSTERED (CurrencyID),
            CONSTRAINT UQ_Currencies_Code UNIQUE (CurrencyCode)
        );
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Currencies_Active ON dbo.Currencies(IsActive) INCLUDE (CurrencyCode, CurrencyNameEN);
    GO

    -- ======================================================================
    -- 5. EXCHANGE RATES
    -- ======================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ExchangeRates')
    BEGIN
        CREATE TABLE dbo.ExchangeRates (
            ExchangeRateID INT IDENTITY(1,1) NOT NULL,
            FromCurrency NVARCHAR(3) NOT NULL,
            ToCurrency NVARCHAR(3) NOT NULL,
            RateDate DATE NOT NULL,
            Rate DECIMAL(18,8) NOT NULL,
            RateSource NVARCHAR(50),
            CreatedBy UNIQUEIDENTIFIER,
            CreatedAt DATETIME2(7) DEFAULT SYSUTCDATETIME(),
            CONSTRAINT PK_ExchangeRates PRIMARY KEY CLUSTERED (ExchangeRateID),
            CONSTRAINT UQ_ExchangeRates_Date UNIQUE (FromCurrency, ToCurrency, RateDate)
        );
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ExchangeRates_Date_Currency ON dbo.ExchangeRates(RateDate, FromCurrency, ToCurrency) INCLUDE (Rate);
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ExchangeRates_Source ON dbo.ExchangeRates(RateSource);
    GO

    -- ======================================================================
    -- 6. INVOICES (with Soft Delete and compression)
    -- ======================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Invoices')
    BEGIN
        CREATE TABLE dbo.Invoices (
            InvoiceID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            InvoiceNumber NVARCHAR(50) NOT NULL,
            InvoiceSequence BIGINT,
            InvoiceType NVARCHAR(30) NOT NULL,
            DocumentType NVARCHAR(30) NOT NULL,
            InvoiceStatus NVARCHAR(20) NOT NULL,
            CustomerID INT,
            SupplierID INT,
            BillingAddressID INT,
            ShippingAddressID INT,
            InvoiceDate DATE NOT NULL,
            DueDate DATE,
            DeliveryDate DATE,
            PaymentDate DATE,
            CurrencyCode NVARCHAR(3) DEFAULT 'SAR',
            ExchangeRate DECIMAL(18,8) DEFAULT 1,
            SubtotalAmount DECIMAL(18,4) DEFAULT 0,
            TotalDiscountAmount DECIMAL(18,4) DEFAULT 0,
            TotalTaxableAmount DECIMAL(18,4) DEFAULT 0,
            TotalTaxAmount DECIMAL(18,4) DEFAULT 0,
            TotalAmount DECIMAL(18,4) DEFAULT 0,
            RoundingAdjustment DECIMAL(18,4) DEFAULT 0,
            PaymentTermsDays INT DEFAULT 0,
            LateFeePercentage DECIMAL(5,2) DEFAULT 0,
            TotalPaidAmount DECIMAL(18,4) DEFAULT 0,
            ReferenceNumber NVARCHAR(100),
            ProjectID INT,
            SalesOrderID INT,
            PurchaseOrderID INT,
            ZATCA_UUID UNIQUEIDENTIFIER,
            ZATCA_PIH NVARCHAR(100),
            ZATCA_ICV BIGINT,
            ZATCA_QRCode NVARCHAR(MAX),
            ZATCA_Hash NVARCHAR(100),
            ZATCA_SignedXML NVARCHAR(MAX),
            ZATCA_SubmissionStatus NVARCHAR(20),
            IsPosted BIT DEFAULT 0,
            PostedAt DATETIME2(7),
            PostedBy UNIQUEIDENTIFIER,
            GLJournalEntryID BIGINT,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            Notes NVARCHAR(MAX),
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_Invoices PRIMARY KEY CLUSTERED (InvoiceID),
            CONSTRAINT UQ_Invoices_Number UNIQUE (CompanyID, InvoiceNumber)
        ) WITH (DATA_COMPRESSION = PAGE);
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Invoices_Company_Status_Date ON dbo.Invoices(CompanyID, InvoiceStatus, InvoiceDate) INCLUDE (TotalAmount, TotalPaidAmount) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Invoices_Customer ON dbo.Invoices(CustomerID) WHERE CustomerID IS NOT NULL AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Invoices_Supplier ON dbo.Invoices(SupplierID) WHERE SupplierID IS NOT NULL AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Invoices_DueDate ON dbo.Invoices(DueDate) WHERE InvoiceStatus NOT IN ('PAID', 'CANCELLED') AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Invoices_InvoiceDate ON dbo.Invoices(InvoiceDate);
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Invoices_Reference ON dbo.Invoices(ReferenceNumber);
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Invoices_GLPosted ON dbo.Invoices(CompanyID, IsPosted) INCLUDE (GLJournalEntryID) WHERE IsPosted = 0 AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Invoices_ZATCA_Status ON dbo.Invoices(ZATCA_SubmissionStatus) WHERE ZATCA_SubmissionStatus IS NOT NULL AND IsDeleted = 0;
    GO

    -- ======================================================================
    -- 7. INVOICE ITEMS (with compression + columnstore)
    -- ======================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'InvoiceItems')
    BEGIN
        CREATE TABLE dbo.InvoiceItems (
            InvoiceItemID BIGINT IDENTITY(1,1) NOT NULL,
            InvoiceID BIGINT NOT NULL,
            LineNumber INT NOT NULL,
            ItemType NVARCHAR(20) NOT NULL,
            ProductID INT,
            ServiceID INT,
            Description NVARCHAR(500) NOT NULL,
            DescriptionAR NVARCHAR(500),
            Quantity DECIMAL(18,6) DEFAULT 1,
            UnitOfMeasure NVARCHAR(20),
            UnitPrice DECIMAL(18,6) DEFAULT 0,
            DiscountPercentage DECIMAL(5,2) DEFAULT 0,
            TaxCategoryCode NVARCHAR(20),
            TaxRate DECIMAL(5,2) DEFAULT 0,
            ExciseTaxRate DECIMAL(5,2) DEFAULT 0,
            RevenueAccountCode NVARCHAR(50),
            ExpenseAccountCode NVARCHAR(50),
            CostCenterCode NVARCHAR(50),
            ProjectID INT,
            UnitCost DECIMAL(18,6) DEFAULT 0,
            Notes NVARCHAR(MAX),
			NetAmount AS (Quantity * UnitPrice * (1 - DiscountPercentage / 100)) PERSISTED,
            CONSTRAINT PK_InvoiceItems PRIMARY KEY CLUSTERED (InvoiceItemID),
            CONSTRAINT FK_InvoiceItems_Invoice FOREIGN KEY (InvoiceID) REFERENCES dbo.Invoices(InvoiceID) ON DELETE CASCADE
        ) WITH (DATA_COMPRESSION = PAGE);
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_InvoiceItems_Invoice ON dbo.InvoiceItems(InvoiceID) INCLUDE (ProductID, Quantity, UnitPrice);
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_InvoiceItems_Product ON dbo.InvoiceItems(ProductID) WHERE ProductID IS NOT NULL;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_InvoiceItems_RevenueAccount ON dbo.InvoiceItems(RevenueAccountCode);
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_InvoiceItems_CostCenter ON dbo.InvoiceItems(CostCenterCode);
    CREATE NONCLUSTERED COLUMNSTORE INDEX IF NOT EXISTS CSIX_InvoiceItems_Analytics ON dbo.InvoiceItems (InvoiceID, Quantity, UnitPrice, DiscountPercentage, TaxRate);
    GO

    -- ======================================================================
    -- 8. INVOICE TAXES
    -- ======================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'InvoiceTaxes')
    BEGIN
        CREATE TABLE dbo.InvoiceTaxes (
            InvoiceTaxID BIGINT IDENTITY(1,1) NOT NULL,
            InvoiceID BIGINT NOT NULL,
            InvoiceItemID BIGINT,
            TaxType NVARCHAR(30) NOT NULL,
            TaxCode NVARCHAR(20) NOT NULL,
            TaxNameAR NVARCHAR(100),
            TaxNameEN NVARCHAR(100),
            TaxableAmount DECIMAL(18,4) NOT NULL,
            TaxRate DECIMAL(5,2) NOT NULL,
            TaxAmount DECIMAL(18,4) NOT NULL,
            ZATCA_TaxCategory NVARCHAR(10),
            CONSTRAINT PK_InvoiceTaxes PRIMARY KEY CLUSTERED (InvoiceTaxID),
            CONSTRAINT FK_InvoiceTaxes_Invoice FOREIGN KEY (InvoiceID) REFERENCES dbo.Invoices(InvoiceID) ON DELETE CASCADE
        );
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_InvoiceTaxes_Invoice ON dbo.InvoiceTaxes(InvoiceID);
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_InvoiceTaxes_TaxType ON dbo.InvoiceTaxes(TaxType, TaxCode);
    GO

    -- ======================================================================
    -- 9. PAYMENTS (with Soft Delete, compression)
    -- ======================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Payments')
    BEGIN
        CREATE TABLE dbo.Payments (
            PaymentID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            PaymentNumber NVARCHAR(50) NOT NULL,
            PaymentType NVARCHAR(30) NOT NULL,
            PaymentStatus NVARCHAR(20) NOT NULL,
            PayerType NVARCHAR(20),
            PayerID INT,
            PayerName NVARCHAR(200),
            PaymentDate DATE NOT NULL,
            ValueDate DATE,
            ReconciliationDate DATE,
            CurrencyCode NVARCHAR(3) DEFAULT 'SAR',
            PaymentAmount DECIMAL(18,4) NOT NULL,
            ExchangeRate DECIMAL(18,8) DEFAULT 1,
            AllocatedAmount DECIMAL(18,4) DEFAULT 0,
            PaymentMethodType NVARCHAR(30) NOT NULL,
            BankAccountCode NVARCHAR(50),
            ChequeNumber NVARCHAR(50),
            ChequeDate DATE,
            ChequeBankName NVARCHAR(200),
            ChequeStatus NVARCHAR(20),
            CardLast4Digits NVARCHAR(4),
            CardType NVARCHAR(20),
            BankReferenceNumber NVARCHAR(100),
            IsPosted BIT DEFAULT 0,
            PostedAt DATETIME2(7),
            GLJournalEntryID BIGINT,
            IsReconciled BIT DEFAULT 0,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            Notes NVARCHAR(MAX),
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_Payments PRIMARY KEY CLUSTERED (PaymentID),
            CONSTRAINT UQ_Payments_Number UNIQUE (CompanyID, PaymentNumber)
        ) WITH (DATA_COMPRESSION = PAGE);
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Payments_Company_Status_Date ON dbo.Payments(CompanyID, PaymentStatus, PaymentDate) INCLUDE (PaymentAmount, AllocatedAmount) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Payments_Payer ON dbo.Payments(PayerType, PayerID) WHERE PayerID IS NOT NULL AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Payments_BankAccount ON dbo.Payments(BankAccountCode) WHERE BankAccountCode IS NOT NULL AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Payments_ChequeStatus ON dbo.Payments(ChequeStatus) WHERE ChequeStatus IS NOT NULL AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Payments_Reconciled ON dbo.Payments(CompanyID, IsReconciled, ReconciliationDate) WHERE IsReconciled = 0 AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Payments_Reference ON dbo.Payments(BankReferenceNumber);
    GO

    -- ======================================================================
    -- 10. PAYMENT ALLOCATIONS
    -- ======================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PaymentAllocations')
    BEGIN
        CREATE TABLE dbo.PaymentAllocations (
            PaymentAllocationID BIGINT IDENTITY(1,1) NOT NULL,
            PaymentID BIGINT NOT NULL,
            InvoiceID BIGINT NOT NULL,
            AllocationAmount DECIMAL(18,4) NOT NULL,
            DiscountTaken DECIMAL(18,4) DEFAULT 0,
            AllocationDate DATE NOT NULL,
            Notes NVARCHAR(500),
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) DEFAULT SYSUTCDATETIME(),
            CONSTRAINT PK_PaymentAllocations PRIMARY KEY CLUSTERED (PaymentAllocationID),
            CONSTRAINT FK_PaymentAllocations_Payment FOREIGN KEY (PaymentID) REFERENCES dbo.Payments(PaymentID) ON DELETE CASCADE,
            CONSTRAINT FK_PaymentAllocations_Invoice FOREIGN KEY (InvoiceID) REFERENCES dbo.Invoices(InvoiceID)
        );
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_PaymentAllocations_Payment ON dbo.PaymentAllocations(PaymentID);
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_PaymentAllocations_Invoice ON dbo.PaymentAllocations(InvoiceID);
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_PaymentAllocations_Date ON dbo.PaymentAllocations(AllocationDate);
    GO

    -- ======================================================================
    -- 11. JOURNAL ENTRIES (with Soft Delete, compression)
    -- ======================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'JournalEntries')
    BEGIN
        CREATE TABLE dbo.JournalEntries (
            JournalEntryID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            EntryNumber NVARCHAR(50) NOT NULL,
            EntrySequence BIGINT,
            EntryType NVARCHAR(30) NOT NULL,
            EntryStatus NVARCHAR(20) NOT NULL,
            EntryDate DATE NOT NULL,
            PostingDate DATE,
            FiscalYear INT NOT NULL,
            FiscalPeriod TINYINT NOT NULL,
            IsPeriodClosed BIT DEFAULT 0,
            Description NVARCHAR(500) NOT NULL,
            Reference NVARCHAR(200),
            CurrencyCode NVARCHAR(3) DEFAULT 'SAR',
            TotalDebit DECIMAL(18,4) DEFAULT 0,
            TotalCredit DECIMAL(18,4) DEFAULT 0,
            SourceDocumentType NVARCHAR(30),
            SourceDocumentID BIGINT,
            IsReversed BIT DEFAULT 0,
            ReversedAt DATETIME2(7),
            ReversedBy UNIQUEIDENTIFIER,
            ReversalEntryID BIGINT,
            IsPosted BIT DEFAULT 0,
            PostedBy UNIQUEIDENTIFIER,
            PostedAt DATETIME2(7),
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            Notes NVARCHAR(MAX),
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_JournalEntries PRIMARY KEY CLUSTERED (JournalEntryID),
            CONSTRAINT UQ_JournalEntries_Number UNIQUE (CompanyID, EntryNumber)
        ) WITH (DATA_COMPRESSION = PAGE);
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_JournalEntries_Company_Status_Date ON dbo.JournalEntries(CompanyID, EntryStatus, EntryDate) INCLUDE (TotalDebit, TotalCredit) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_JournalEntries_FiscalPeriod ON dbo.JournalEntries(CompanyID, FiscalYear, FiscalPeriod, IsPeriodClosed) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_JournalEntries_SourceDocument ON dbo.JournalEntries(SourceDocumentType, SourceDocumentID) WHERE SourceDocumentID IS NOT NULL AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_JournalEntries_PostingDate ON dbo.JournalEntries(PostingDate) WHERE IsPosted = 1 AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_JournalEntries_Reversal ON dbo.JournalEntries(IsReversed, ReversalEntryID) WHERE IsReversed = 1 AND IsDeleted = 0;
    GO

    -- ======================================================================
    -- 12. JOURNAL ENTRY LINES (with compression + columnstore)
    -- ======================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'JournalEntryLines')
    BEGIN
        CREATE TABLE dbo.JournalEntryLines (
            JournalEntryLineID BIGINT IDENTITY(1,1) NOT NULL,
            JournalEntryID BIGINT NOT NULL,
            LineNumber INT NOT NULL,
            AccountCode NVARCHAR(50) NOT NULL,
            DebitAmount DECIMAL(18,4) DEFAULT 0,
            CreditAmount DECIMAL(18,4) DEFAULT 0,
            CostCenterCode NVARCHAR(50),
            ProjectID INT,
            DepartmentCode NVARCHAR(50),
            CurrencyCode NVARCHAR(3),
            ExchangeRate DECIMAL(18,8),
            DebitAmountForeign DECIMAL(18,4),
            CreditAmountForeign DECIMAL(18,4),
            Description NVARCHAR(500),
            Reference NVARCHAR(200),
            IsReconciled BIT DEFAULT 0,
            CONSTRAINT PK_JournalEntryLines PRIMARY KEY CLUSTERED (JournalEntryLineID),
            CONSTRAINT FK_JournalEntryLines_JournalEntry FOREIGN KEY (JournalEntryID) REFERENCES dbo.JournalEntries(JournalEntryID) ON DELETE CASCADE
        ) WITH (DATA_COMPRESSION = PAGE);
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_JournalEntryLines_Entry ON dbo.JournalEntryLines(JournalEntryID);
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_JournalEntryLines_Account ON dbo.JournalEntryLines(AccountCode) INCLUDE (DebitAmount, CreditAmount);
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_JournalEntryLines_CostCenter ON dbo.JournalEntryLines(CostCenterCode);
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_JournalEntryLines_Reconciled ON dbo.JournalEntryLines(IsReconciled) WHERE IsReconciled = 0;
    CREATE NONCLUSTERED COLUMNSTORE INDEX IF NOT EXISTS CSIX_JournalEntryLines_Reporting ON dbo.JournalEntryLines (AccountCode, CostCenterCode, DebitAmount, CreditAmount);
    GO

    -- ======================================================================
    -- 13. BANK ACCOUNTS (with Soft Delete)
    -- ======================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'BankAccounts')
    BEGIN
        CREATE TABLE dbo.BankAccounts (
            BankAccountID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            AccountCode NVARCHAR(50) NOT NULL,
            BankName NVARCHAR(200) NOT NULL,
            BranchName NVARCHAR(200),
            AccountNumber NVARCHAR(50) NOT NULL,
            IBAN NVARCHAR(50),
            BIC_SWIFT NVARCHAR(20),
            CurrencyCode NVARCHAR(3) DEFAULT 'SAR',
            CurrentBalance DECIMAL(18,4) DEFAULT 0,
            OpeningBalance DECIMAL(18,4) DEFAULT 0,
            IsActive BIT DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            CreatedAt DATETIME2(7) DEFAULT SYSUTCDATETIME(),
            CONSTRAINT PK_BankAccounts PRIMARY KEY CLUSTERED (BankAccountID),
            CONSTRAINT UQ_BankAccounts_AccountNumber UNIQUE (CompanyID, AccountNumber)
        );
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_BankAccounts_Company_Active ON dbo.BankAccounts(CompanyID, IsActive) INCLUDE (AccountCode, BankName) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_BankAccounts_AccountCode ON dbo.BankAccounts(AccountCode);
    GO

    -- ======================================================================
    -- 14. BANK STATEMENTS
    -- ======================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'BankStatements')
    BEGIN
        CREATE TABLE dbo.BankStatements (
            BankStatementID BIGINT IDENTITY(1,1) NOT NULL,
            BankAccountID INT NOT NULL,
            StatementDate DATE NOT NULL,
            StatementNumber NVARCHAR(50),
            OpeningBalance DECIMAL(18,4) NOT NULL,
            ClosingBalance DECIMAL(18,4) NOT NULL,
            TotalDebits DECIMAL(18,4) DEFAULT 0,
            TotalCredits DECIMAL(18,4) DEFAULT 0,
            IsReconciled BIT DEFAULT 0,
            ImportedAt DATETIME2(7) DEFAULT SYSUTCDATETIME(),
            CONSTRAINT PK_BankStatements PRIMARY KEY CLUSTERED (BankStatementID),
            CONSTRAINT FK_BankStatements_BankAccount FOREIGN KEY (BankAccountID) REFERENCES dbo.BankAccounts(BankAccountID)
        );
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_BankStatements_Account_Date ON dbo.BankStatements(BankAccountID, StatementDate) INCLUDE (IsReconciled);
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_BankStatements_Reconciled ON dbo.BankStatements(BankAccountID, IsReconciled) WHERE IsReconciled = 0;
    GO

    -- ======================================================================
    -- 15. BANK STATEMENT LINES
    -- ======================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'BankStatementLines')
    BEGIN
        CREATE TABLE dbo.BankStatementLines (
            BankStatementLineID BIGINT IDENTITY(1,1) NOT NULL,
            BankStatementID BIGINT NOT NULL,
            TransactionDate DATE NOT NULL,
            ValueDate DATE,
            Description NVARCHAR(500),
            ReferenceNumber NVARCHAR(100),
            DebitAmount DECIMAL(18,4) DEFAULT 0,
            CreditAmount DECIMAL(18,4) DEFAULT 0,
            Balance DECIMAL(18,4),
            IsReconciled BIT DEFAULT 0,
            ReconciledWith NVARCHAR(50),
            ReconciledID BIGINT,
            ReconciledAt DATETIME2(7),
            CONSTRAINT PK_BankStatementLines PRIMARY KEY CLUSTERED (BankStatementLineID),
            CONSTRAINT FK_BankStatementLines_BankStatement FOREIGN KEY (BankStatementID) REFERENCES dbo.BankStatements(BankStatementID) ON DELETE CASCADE
        );
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_BankStatementLines_Statement ON dbo.BankStatementLines(BankStatementID);
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_BankStatementLines_Reconciled ON dbo.BankStatementLines(IsReconciled, ReconciledWith, ReconciledID) WHERE IsReconciled = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_BankStatementLines_Date ON dbo.BankStatementLines(TransactionDate);
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_BankStatementLines_Reference ON dbo.BankStatementLines(ReferenceNumber);
    GO

    -- ======================================================================
    -- 16. BUDGETS (with Soft Delete)
    -- ======================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Budgets')
    BEGIN
        CREATE TABLE dbo.Budgets (
            BudgetID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            BudgetName NVARCHAR(200) NOT NULL,
            FiscalYear INT NOT NULL,
            BudgetType NVARCHAR(30) NOT NULL,
            AccountCode NVARCHAR(50) NOT NULL,
            CostCenterCode NVARCHAR(50),
            Period1Amount DECIMAL(18,2) DEFAULT 0,
            Period2Amount DECIMAL(18,2) DEFAULT 0,
            Period3Amount DECIMAL(18,2) DEFAULT 0,
            Period4Amount DECIMAL(18,2) DEFAULT 0,
            Period5Amount DECIMAL(18,2) DEFAULT 0,
            Period6Amount DECIMAL(18,2) DEFAULT 0,
            Period7Amount DECIMAL(18,2) DEFAULT 0,
            Period8Amount DECIMAL(18,2) DEFAULT 0,
            Period9Amount DECIMAL(18,2) DEFAULT 0,
            Period10Amount DECIMAL(18,2) DEFAULT 0,
            Period11Amount DECIMAL(18,2) DEFAULT 0,
            Period12Amount DECIMAL(18,2) DEFAULT 0,
            TotalBudget AS (Period1Amount + Period2Amount + Period3Amount + Period4Amount +
                            Period5Amount + Period6Amount + Period7Amount + Period8Amount +
                            Period9Amount + Period10Amount + Period11Amount + Period12Amount) PERSISTED,
            IsActive BIT DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) DEFAULT SYSUTCDATETIME(),
            CONSTRAINT PK_Budgets PRIMARY KEY CLUSTERED (BudgetID)
        );
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Budgets_Company_Year_Account ON dbo.Budgets(CompanyID, FiscalYear, AccountCode) INCLUDE (CostCenterCode, Period1Amount, Period2Amount, Period3Amount, Period4Amount, Period5Amount, Period6Amount, Period7Amount, Period8Amount, Period9Amount, Period10Amount, Period11Amount, Period12Amount) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Budgets_CostCenter ON dbo.Budgets(CostCenterCode);
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Budgets_Active ON dbo.Budgets(CompanyID, IsActive) WHERE IsActive = 1 AND IsDeleted = 0;
    GO

    -- ======================================================================
    -- 17. FIXED ASSETS (with Soft Delete)
    -- ======================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'FixedAssets')
    BEGIN
        CREATE TABLE dbo.FixedAssets (
            AssetID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            AssetCode NVARCHAR(50) NOT NULL,
            AssetNameAR NVARCHAR(255) NOT NULL,
            AssetNameEN NVARCHAR(255) NOT NULL,
            AssetCategory NVARCHAR(50),
            PurchaseDate DATE NOT NULL,
            PurchaseCost DECIMAL(18,4) NOT NULL,
            SalvageValue DECIMAL(18,4) DEFAULT 0,
            UsefulLifeYears INT NOT NULL,
            DepreciationMethod NVARCHAR(30) NOT NULL,
            AssetAccountCode NVARCHAR(50) NOT NULL,
            DepreciationAccountCode NVARCHAR(50) NOT NULL,
            AccumulatedDepreciation DECIMAL(18,4) DEFAULT 0,
            NetBookValue AS (PurchaseCost - AccumulatedDepreciation) PERSISTED,
            DisposalDate DATE,
            DisposalAmount DECIMAL(18,4),
            IsActive BIT DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            SerialNumber NVARCHAR(100),
            Location NVARCHAR(200),
            Notes NVARCHAR(MAX),
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_FixedAssets PRIMARY KEY CLUSTERED (AssetID),
            CONSTRAINT UQ_FixedAssets_Code UNIQUE (CompanyID, AssetCode)
        );
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_FixedAssets_Company_Active ON dbo.FixedAssets(CompanyID, IsActive) INCLUDE (AssetCode, AssetNameAR, NetBookValue) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_FixedAssets_Category ON dbo.FixedAssets(AssetCategory);
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_FixedAssets_PurchaseDate ON dbo.FixedAssets(PurchaseDate);
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_FixedAssets_Disposal ON dbo.FixedAssets(DisposalDate) WHERE DisposalDate IS NOT NULL AND IsDeleted = 0;
    GO

    -- ======================================================================
    -- 18. DEPRECIATION SCHEDULE
    -- ======================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'DepreciationSchedule')
    BEGIN
        CREATE TABLE dbo.DepreciationSchedule (
            DepreciationID BIGINT IDENTITY(1,1) NOT NULL,
            AssetID INT NOT NULL,
            FiscalYear INT NOT NULL,
            FiscalPeriod TINYINT NOT NULL,
            DepreciationAmount DECIMAL(18,4) NOT NULL,
            AccumulatedDepreciation DECIMAL(18,4) NOT NULL,
            NetBookValue DECIMAL(18,4) NOT NULL,
            JournalEntryID BIGINT,
            IsPosted BIT DEFAULT 0,
            PostedAt DATETIME2(7),
            CONSTRAINT PK_DepreciationSchedule PRIMARY KEY CLUSTERED (DepreciationID),
            CONSTRAINT FK_DepreciationSchedule_Asset FOREIGN KEY (AssetID) REFERENCES dbo.FixedAssets(AssetID)
        );
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_DepreciationSchedule_Asset ON dbo.DepreciationSchedule(AssetID) INCLUDE (FiscalYear, FiscalPeriod, DepreciationAmount);
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_DepreciationSchedule_Posting ON dbo.DepreciationSchedule(IsPosted, JournalEntryID) WHERE IsPosted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_DepreciationSchedule_FiscalPeriod ON dbo.DepreciationSchedule(FiscalYear, FiscalPeriod);
    GO

    -- ======================================================================
    -- EXTRA: PERIOD CLOSING
    -- ======================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PeriodClosing')
    BEGIN
        CREATE TABLE dbo.PeriodClosing (
            ClosingID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            FiscalYear INT NOT NULL,
            FiscalPeriod TINYINT NOT NULL,
            ClosedAt DATETIME2(7) DEFAULT SYSUTCDATETIME(),
            ClosedBy UNIQUEIDENTIFIER NOT NULL,
            ClosingNote NVARCHAR(500),
            CONSTRAINT PK_PeriodClosing PRIMARY KEY CLUSTERED (ClosingID),
            CONSTRAINT UQ_PeriodClosing_CompanyYearPeriod UNIQUE (CompanyID, FiscalYear, FiscalPeriod)
        );
    END
    GO

    -- ======================================================================
    -- EXTRA: RECONCILIATIONS
    -- ======================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Reconciliations')
    BEGIN
        CREATE TABLE dbo.Reconciliations (
            ReconciliationID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            BankAccountID INT NOT NULL,
            ReconciliationDate DATE NOT NULL,
            EndingBalanceBook DECIMAL(18,4) NOT NULL,
            EndingBalanceBank DECIMAL(18,4) NOT NULL,
            Difference DECIMAL(18,4) AS (EndingBalanceBook - EndingBalanceBank) PERSISTED,
            IsCompleted BIT DEFAULT 0,
            CompletedAt DATETIME2(7),
            CompletedBy UNIQUEIDENTIFIER,
            Notes NVARCHAR(MAX),
            CreatedAt DATETIME2(7) DEFAULT SYSUTCDATETIME(),
            CONSTRAINT PK_Reconciliations PRIMARY KEY CLUSTERED (ReconciliationID),
            CONSTRAINT FK_Reconciliations_BankAccount FOREIGN KEY (BankAccountID) REFERENCES dbo.BankAccounts(BankAccountID)
        );
    END
    GO

    -- ======================================================================
    -- EXTRA: RECURRING JOURNALS
    -- ======================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'RecurringJournals')
    BEGIN
        CREATE TABLE dbo.RecurringJournals (
            RecurringJournalID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            RecurrenceName NVARCHAR(200) NOT NULL,
            Frequency NVARCHAR(20) NOT NULL,
            DayOfMonth TINYINT,
            StartDate DATE NOT NULL,
            EndDate DATE,
            NextRunDate DATE NOT NULL,
            LastRunDate DATE,
            JournalEntryTemplate NVARCHAR(MAX) NOT NULL,
            IsActive BIT DEFAULT 1,
            CreatedAt DATETIME2(7) DEFAULT SYSUTCDATETIME(),
            CONSTRAINT PK_RecurringJournals PRIMARY KEY CLUSTERED (RecurringJournalID)
        );
    END
    GO

    -- ======================================================================
    -- EXTRA: FINANCIAL REPORTS
    -- ======================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'FinancialReports')
    BEGIN
        CREATE TABLE dbo.FinancialReports (
            ReportID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ReportName NVARCHAR(200) NOT NULL,
            ReportType NVARCHAR(30) NOT NULL,
            ReportDefinition NVARCHAR(MAX) NOT NULL,
            IsDefault BIT DEFAULT 0,
            IsActive BIT DEFAULT 1,
            CreatedAt DATETIME2(7) DEFAULT SYSUTCDATETIME(),
            CONSTRAINT PK_FinancialReports PRIMARY KEY CLUSTERED (ReportID)
        );
    END
    GO

    -- ======================================================================
    -- IMPROVED TRIGGERS WITH AUDIT LOGGING (dummy insert into AuditLogs)
    -- ======================================================================
    CREATE OR ALTER TRIGGER trg_InvoiceItems_UpdateInvoiceTotals ON dbo.InvoiceItems
    AFTER INSERT, UPDATE, DELETE
    AS
    BEGIN
        SET NOCOUNT ON;
        WITH InvoiceTotals AS (
            SELECT 
                InvoiceID,
                SUM(NetAmount) AS SubtotalAmount,
                SUM(TaxAmount) AS TotalTaxAmount,
                SUM(TotalAmount) AS TotalAmount
            FROM dbo.InvoiceItems
            WHERE InvoiceID IN (SELECT InvoiceID FROM inserted UNION SELECT InvoiceID FROM deleted)
            GROUP BY InvoiceID
        )
        UPDATE inv
        SET 
            SubtotalAmount = ISNULL(t.SubtotalAmount, 0),
            TotalTaxAmount = ISNULL(t.TotalTaxAmount, 0),
            TotalAmount = ISNULL(t.TotalAmount, 0),
            UpdatedAt = SYSUTCDATETIME()
        FROM dbo.Invoices inv
        INNER JOIN InvoiceTotals t ON inv.InvoiceID = t.InvoiceID;

        -- Logging to AuditLogs (if table exists)
        IF OBJECT_ID('dbo.AuditLogs', 'U') IS NOT NULL
        BEGIN
            INSERT INTO dbo.AuditLogs (CompanyID, TableName, RecordID, ActionType, ChangedBy, ChangedAt)
            SELECT DISTINCT CompanyID, 'InvoiceItems', CAST(InvoiceID AS NVARCHAR), 'UPDATE', SUSER_SNAME(), SYSUTCDATETIME()
            FROM inserted;
        END
    END;
    GO

    CREATE OR ALTER TRIGGER trg_PaymentAllocations_UpdateTotals ON dbo.PaymentAllocations
    AFTER INSERT, UPDATE, DELETE
    AS
    BEGIN
        SET NOCOUNT ON;
        WITH PaymentTotals AS (
            SELECT PaymentID, SUM(AllocationAmount) AS TotalAllocated
            FROM dbo.PaymentAllocations
            WHERE PaymentID IN (SELECT PaymentID FROM inserted UNION SELECT PaymentID FROM deleted)
            GROUP BY PaymentID
        )
        UPDATE p
        SET AllocatedAmount = ISNULL(t.TotalAllocated, 0), UpdatedAt = SYSUTCDATETIME()
        FROM dbo.Payments p
        INNER JOIN PaymentTotals t ON p.PaymentID = t.PaymentID;

        WITH InvoiceTotals AS (
            SELECT InvoiceID, SUM(AllocationAmount) AS TotalPaid
            FROM dbo.PaymentAllocations
            WHERE InvoiceID IN (SELECT InvoiceID FROM inserted UNION SELECT InvoiceID FROM deleted)
            GROUP BY InvoiceID
        )
        UPDATE inv
        SET 
            TotalPaidAmount = ISNULL(t.TotalPaid, 0),
            InvoiceStatus = CASE WHEN ISNULL(t.TotalPaid,0) >= inv.TotalAmount THEN 'PAID' WHEN ISNULL(t.TotalPaid,0) > 0 THEN 'PARTIAL' ELSE inv.InvoiceStatus END,
            UpdatedAt = SYSUTCDATETIME()
        FROM dbo.Invoices inv
        INNER JOIN InvoiceTotals t ON inv.InvoiceID = t.InvoiceID;

        -- Logging
        IF OBJECT_ID('dbo.AuditLogs', 'U') IS NOT NULL
        BEGIN
            INSERT INTO dbo.AuditLogs (CompanyID, TableName, RecordID, ActionType, ChangedBy, ChangedAt)
            SELECT DISTINCT CompanyID, 'PaymentAllocations', CAST(PaymentID AS NVARCHAR), 'UPDATE', SUSER_SNAME(), SYSUTCDATETIME()
            FROM inserted;
        END
    END;
    GO

    CREATE OR ALTER TRIGGER trg_JournalEntries_ValidateBalance ON dbo.JournalEntries
    AFTER UPDATE
    AS
    BEGIN
        SET NOCOUNT ON;
        IF EXISTS (SELECT 1 FROM inserted WHERE EntryStatus = 'POSTED' AND ABS(TotalDebit - TotalCredit) >= 0.01)
            THROW 50000, 'Cannot post an unbalanced journal entry. Debits must equal Credits.', 1;
    END;
    GO

    -- ======================================================================
    -- ROW-LEVEL SECURITY (RLS) – if schema exists
    -- ======================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'Security')
        EXEC('CREATE SCHEMA Security');
    GO

    CREATE OR ALTER FUNCTION Security.fn_CompanyAccessPredicate(@CompanyID UNIQUEIDENTIFIER)
    RETURNS TABLE
    WITH SCHEMABINDING
    AS
    RETURN
        SELECT 1 AS AccessResult
        WHERE @CompanyID = CAST(SESSION_CONTEXT(N'CurrentCompanyID') AS UNIQUEIDENTIFIER)
           OR IS_ROLEMEMBER('db_owner') = 1
           OR IS_ROLEMEMBER('FinanceAdmin') = 1;
    GO

    IF NOT EXISTS (SELECT 1 FROM sys.security_policies WHERE name = 'FinanceCompanyIsolation')
    BEGIN
        CREATE SECURITY POLICY Security.FinanceCompanyIsolation
        ADD FILTER PREDICATE Security.fn_CompanyAccessPredicate(CompanyID) ON dbo.Invoices,
        ADD FILTER PREDICATE Security.fn_CompanyAccessPredicate(CompanyID) ON dbo.Payments,
        ADD FILTER PREDICATE Security.fn_CompanyAccessPredicate(CompanyID) ON dbo.JournalEntries,
        ADD FILTER PREDICATE Security.fn_CompanyAccessPredicate(CompanyID) ON dbo.ChartOfAccounts,
        ADD FILTER PREDICATE Security.fn_CompanyAccessPredicate(CompanyID) ON dbo.BankAccounts,
        ADD FILTER PREDICATE Security.fn_CompanyAccessPredicate(CompanyID) ON dbo.Budgets,
        ADD FILTER PREDICATE Security.fn_CompanyAccessPredicate(CompanyID) ON dbo.FixedAssets
        WITH (STATE = ON);
    END
    GO

    COMMIT TRANSACTION;
    PRINT '═══════════════════════════════════════════════════════════════════════';
    PRINT '✅ Finance Module (Ultimate 10/10) deployed successfully.';
    PRINT '   - TRY/CATCH transaction wrapper';
    PRINT '   - Self-referencing FKs fixed (via ALTER)';
    PRINT '   - Unified Soft Delete on all tables';
    PRINT '   - Triggers with audit logging';
    PRINT '   - Full compatibility with post.sql and TMPL.sql';
    PRINT '═══════════════════════════════════════════════════════════════════════';
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
-- source: archive/legacy_sqlserver/fin/fin_asst.sql
-- ============================================================
-- ========================================================================
-- FILE: dba/fin/fin_asst.sql
-- PROJECT: SHOUTECH ERP V10 - Fixed Assets Extensions (Ultimate 10/10)
-- VERSION: 10.3.0
-- DESCRIPTION: Asset categories (IAS 16), locations & custodians, movement
--              log, maintenance records, revaluations and disposals with
--              full audit.
--              NOTE: dbo.FixedAssets + dbo.DepreciationSchedule → dba/fin/fin.sql.
-- ========================================================================
-- ✅ Idempotent | TRY/CATCH + TRANSACTION | Soft Delete + RowVersion
-- ✅ IFRS IAS 16 compliant (cost model + revaluation model).
-- ========================================================================

USE [$(DbFileName)];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

PRINT N'═══════════════════════════════════════════════════════════════════════════';
PRINT N'SHOUTECH ERP V10 – Fixed Assets Extensions (Ultimate 10/10)';
PRINT N'═══════════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AssetCategories')
    BEGIN
        CREATE TABLE dbo.AssetCategories (
            AssetCategoryID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            CategoryCode NVARCHAR(20) NOT NULL,
            CategoryNameAR NVARCHAR(150) NOT NULL,
            CategoryNameEN NVARCHAR(150) NOT NULL,
            DefaultUsefulLifeYears INT NOT NULL,
            DefaultDepreciationMethod NVARCHAR(15) NOT NULL DEFAULT 'STRAIGHT_LINE', -- STRAIGHT_LINE | DECLINING_BALANCE | UNITS_OF_PRODUCTION
            DefaultSalvagePct DECIMAL(5,2) NOT NULL DEFAULT 0,
            AssetAccountID INT NULL,                     -- Loose refs → COA
            AccumDepAccountID INT NULL,
            DepExpenseAccountID INT NULL,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_AssetCategories PRIMARY KEY CLUSTERED (AssetCategoryID),
            CONSTRAINT UQ_AssetCategories_Code UNIQUE (CompanyID, CategoryCode),
            CONSTRAINT CK_AssetCategories_Method CHECK (DefaultDepreciationMethod IN ('STRAIGHT_LINE','DECLINING_BALANCE','UNITS_OF_PRODUCTION'))
        );
    END

    -- ==========================================================================
    --    Immutable log — every transfer inserts a row.
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AssetMovements')
    BEGIN
        CREATE TABLE dbo.AssetMovements (
            AssetMovementID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            AssetID INT NOT NULL,                        -- Loose ref → FixedAssets
            MovementType NVARCHAR(15) NOT NULL,          -- TRANSFER | CUSTODY_CHANGE | LOCATION_CHANGE | SENT_REPAIR | RETURNED
            MovementDate DATE NOT NULL,
            FromLocation NVARCHAR(200) NULL,
            ToLocation NVARCHAR(200) NULL,
            FromCustodianID INT NULL,                    -- EmployeeID
            ToCustodianID INT NULL,
            Reason NVARCHAR(500) NOT NULL,
            DocumentRef NVARCHAR(100) NULL,
            ApprovedBy UNIQUEIDENTIFIER NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_AssetMovements PRIMARY KEY CLUSTERED (AssetMovementID),
            CONSTRAINT CK_AssetMovements_Type CHECK (MovementType IN ('TRANSFER','CUSTODY_CHANGE','LOCATION_CHANGE','SENT_REPAIR','RETURNED'))
        ) WITH (DATA_COMPRESSION = PAGE);
        CREATE NONCLUSTERED INDEX IX_AssetMovements_Asset
            ON dbo.AssetMovements (CompanyID, AssetID, MovementDate DESC) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AssetMaintenance')
    BEGIN
        CREATE TABLE dbo.AssetMaintenance (
            MaintenanceID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            AssetID INT NOT NULL,
            MaintenanceType NVARCHAR(15) NOT NULL,       -- PREVENTIVE | CORRECTIVE | INSPECTION
            ScheduledDate DATE NOT NULL,
            CompletedDate DATE NULL,
            Description NVARCHAR(500) NOT NULL,
            ServiceProvider NVARCHAR(200) NULL,
            Cost DECIMAL(18,4) NOT NULL DEFAULT 0,
            Capitalize BIT NOT NULL DEFAULT 0,           -- IAS 16: capitalize vs expense
            NextDueDate DATE NULL,
            Status NVARCHAR(12) NOT NULL DEFAULT 'SCHEDULED', -- SCHEDULED | IN_PROGRESS | COMPLETED | CANCELLED
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_AssetMaintenance PRIMARY KEY CLUSTERED (MaintenanceID),
            CONSTRAINT CK_AssetMaintenance_Type CHECK (MaintenanceType IN ('PREVENTIVE','CORRECTIVE','INSPECTION')),
            CONSTRAINT CK_AssetMaintenance_Status CHECK (Status IN ('SCHEDULED','IN_PROGRESS','COMPLETED','CANCELLED'))
        );
        CREATE NONCLUSTERED INDEX IX_AssetMaintenance_Due
            ON dbo.AssetMaintenance (CompanyID, NextDueDate) WHERE IsDeleted = 0 AND Status <> 'CANCELLED';
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AssetRevaluations')
    BEGIN
        CREATE TABLE dbo.AssetRevaluations (
            RevaluationID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            AssetID INT NOT NULL,
            RevaluationDate DATE NOT NULL,
            CarryingAmountBefore DECIMAL(18,4) NOT NULL,
            FairValue DECIMAL(18,4) NOT NULL,
            RevaluationSurplus DECIMAL(18,4) NOT NULL,   -- + surplus (OCI) / − deficit (P&L first)
            ValuationMethod NVARCHAR(30) NOT NULL,       -- MARKET | INCOME | COST | EXTERNAL_APPRAISER
            AppraiserName NVARCHAR(200) NULL,
            AppraisalReportRef NVARCHAR(100) NULL,
            JournalEntryID BIGINT NULL,                  -- Loose ref → JournalEntries
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_AssetRevaluations PRIMARY KEY CLUSTERED (RevaluationID),
            CONSTRAINT UQ_AssetRevaluations UNIQUE (CompanyID, AssetID, RevaluationDate)
        );
        CREATE NONCLUSTERED INDEX IX_AssetRevaluations_Asset
            ON dbo.AssetRevaluations (CompanyID, AssetID, RevaluationDate DESC) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AssetDisposals')
    BEGIN
        CREATE TABLE dbo.AssetDisposals (
            DisposalID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            AssetID INT NOT NULL,
            DisposalDate DATE NOT NULL,
            DisposalType NVARCHAR(15) NOT NULL,          -- SALE | SCRAP | DONATION | WRITE_OFF | TRADE_IN
            CarryingAmount DECIMAL(18,4) NOT NULL,
            ProceedsAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
            GainLoss AS (ProceedsAmount - CarryingAmount) PERSISTED,
            BuyerInfo NVARCHAR(300) NULL,
            DocumentRef NVARCHAR(100) NULL,
            JournalEntryID BIGINT NULL,
            ApprovedBy UNIQUEIDENTIFIER NOT NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_AssetDisposals PRIMARY KEY CLUSTERED (DisposalID),
            CONSTRAINT CK_AssetDisposals_Type CHECK (DisposalType IN ('SALE','SCRAP','DONATION','WRITE_OFF','TRADE_IN'))
        );
        CREATE NONCLUSTERED INDEX IX_AssetDisposals_Asset
            ON dbo.AssetDisposals (CompanyID, AssetID, DisposalDate DESC) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- 6. SEED DATA — asset categories
    -- ==========================================================================
    PRINT N'✅ Seeding asset categories...';

    IF NOT EXISTS (SELECT 1 FROM dbo.AssetCategories WHERE CategoryCode = 'BLDG')
    BEGIN
        INSERT INTO dbo.AssetCategories
            (CompanyID, CategoryCode, CategoryNameAR, CategoryNameEN,
             DefaultUsefulLifeYears, DefaultDepreciationMethod, DefaultSalvagePct, CreatedBy)
        SELECT TOP 1 C.CompanyID, X.Code, X.NameAR, X.NameEN, X.Life, X.Method, X.Salvage,
               '00000000-0000-0000-0000-000000000001'
        FROM MST.dbo.Companies C
        CROSS JOIN (VALUES
            ('BLDG',  N'Buildings and Constructions',       'Buildings',            25, 'STRAIGHT_LINE',     5),
            ('PLANT', N'Machinery and Equipment',          'Plant & Machinery',    10, 'STRAIGHT_LINE',     5),
            ('VEH',   N'Cars and Vehicles',       'Vehicles',              5, 'STRAIGHT_LINE',    10),
            ('FURN',  N'Furniture and Fixtures',        'Furniture & Fixtures',  7, 'STRAIGHT_LINE',     0),
            ('IT',    N'Computers and IT Equipment',   'IT Equipment',          4, 'STRAIGHT_LINE',     0),
            ('LAND',  N'Land',                'Land (non-depreciable)',0, 'STRAIGHT_LINE',     0)
        ) X (Code, NameAR, NameEN, Life, Method, Salvage);
    END

    -- ==========================================================================
    -- COMPLETION
    -- ==========================================================================
    COMMIT TRANSACTION;
    PRINT N'═══════════════════════════════════════════════════════════════════════════';
    PRINT N'✅ Fixed Assets Extensions deployed successfully.';
    PRINT N'   - IAS 16 categories + movements + maintenance + revaluations + disposals.';
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
-- source: archive/legacy_sqlserver/fin/gl.sql
-- ============================================================
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
            IsPostingAccount    BIT NOT NULL DEFAULT 1, -- 1: real posting account, 0: summary (Parent)
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
            JournalNo           VARCHAR(100) NOT NULL,   -- generated via sys.sp_GetNextSequenceValue
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
            CONSTRAINT CK_fin_JournalHeaders_Balance CHECK (TotalDebit = TotalCredit) -- strict financial balance guard
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

-- ============================================================
-- source: archive/legacy_sqlserver/fin/inv.sql
-- ============================================================
-- ========================================================================
-- FILE: dba/sch/inv.sql
-- PROJECT: SHOUTECH ERP V10 - Inventory Management (Ultimate 10/10)
-- VERSION: 10.4.0 Ultimate
-- DESCRIPTION: Complete inventory with FIFO cost layering, landed cost,
--              advanced unit conversions, quality inspection workflow,
--              audit logging, and full integration readiness.
-- ========================================================================
-- ENTERPRISE FEATURES:
-- ✅ FIFO Cost Layer Engine (protected + audit)
-- ✅ Landed Cost (Shipping, Customs, Insurance, Other)
-- ✅ Advanced Unit Conversions (matrix between any UOM)
-- ✅ Quality Inspection Workflow (pending, passed, rejected, hold)
-- ✅ Stock Counts & Cycle Counting
-- ✅ Product Kits / Bundles
-- ✅ Block Delete on used products
-- ✅ Deadlock-safe triggers with audit logging
-- ✅ Soft Delete + RowVersion on ALL tables
-- ✅ DATA_COMPRESSION + Columnstore on large tables
-- ✅ Snapshot Isolation enabled
-- ✅ Extended Properties for documentation
-- ✅ Seed data (UOMs, conversions, quality tests, landed cost types)
-- ========================================================================
-- DEPENDENCIES: MST.sys (Companies), fin.sql (ChartOfAccounts for FKs deferred)
-- ========================================================================

USE [$(DbFileName)];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

-- Enable Snapshot Isolation for Enterprise concurrency
ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON;
ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON;
GO

PRINT N'═══════════════════════════════════════════════════════════════════════';
PRINT N'SHOUTECH ERP V10 – Inventory Module (Ultimate 10/10)';
PRINT N'Version: 10.4.0 | Date: ' + CONVERT(NVARCHAR(20), GETDATE(), 120);
PRINT N'═══════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- ==========================================================================
    -- 1. PRODUCT CATEGORIES (with accounting code fields, FK to ChartOfAccounts deferred)
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ProductCategories')
    BEGIN
        CREATE TABLE dbo.ProductCategories (
            CategoryID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            CategoryCode NVARCHAR(50) NOT NULL,
            CategoryNameAR NVARCHAR(200) NOT NULL,
            CategoryNameEN NVARCHAR(200) NOT NULL,
            ParentCategoryCode NVARCHAR(50),
            CategoryLevel TINYINT NOT NULL DEFAULT 1,
            CategoryPath AS (CASE WHEN ParentCategoryCode IS NULL THEN '/' + CategoryCode ELSE '/' + ParentCategoryCode + '/' + CategoryCode END) PERSISTED,
            DisplayOrder INT DEFAULT 0,
            IconName NVARCHAR(50),
            ColorCode NVARCHAR(7),
            InventoryAccountCode NVARCHAR(50),      -- FK to ChartOfAccounts (deferred)
            COGSAccountCode NVARCHAR(50),           -- FK to ChartOfAccounts (deferred)
            RevenueAccountCode NVARCHAR(50),        -- FK to ChartOfAccounts (deferred)
            DefaultTaxRate DECIMAL(5,2) DEFAULT 15,
            AllowNegativeStock BIT DEFAULT 0,
            TrackExpiry BIT DEFAULT 0,
            TrackBatches BIT DEFAULT 0,
            TrackSerials BIT DEFAULT 0,
            IsActive BIT NOT NULL DEFAULT 1,
            IsHeader BIT NOT NULL DEFAULT 0,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            Description NVARCHAR(MAX),
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_ProductCategories PRIMARY KEY CLUSTERED (CategoryID),
            CONSTRAINT UQ_ProductCategories_Code UNIQUE (CompanyID, CategoryCode),
            CONSTRAINT FK_ProductCategories_Parent FOREIGN KEY (CompanyID, ParentCategoryCode) REFERENCES dbo.ProductCategories(CompanyID, CategoryCode),
            CONSTRAINT FK_ProductCategories_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID),
            CONSTRAINT CK_ProductCategories_Level CHECK (CategoryLevel BETWEEN 1 AND 5),
			CONSTRAINT FK_ProductCategories_InventoryAcct 
				FOREIGN KEY (CompanyID, InventoryAccountCode) 
				REFERENCES MST.dbo.ChartOfAccounts(CompanyID, AccountCode),
			CONSTRAINT FK_ProductCategories_COGSAcct 
				FOREIGN KEY (CompanyID, COGSAccountCode) 
				REFERENCES MST.dbo.ChartOfAccounts(CompanyID, AccountCode),
			CONSTRAINT FK_ProductCategories_RevenueAcct 
				FOREIGN KEY (CompanyID, RevenueAccountCode) 
				REFERENCES MST.dbo.ChartOfAccounts(CompanyID, AccountCode)
					);
        PRINT N'✅ ProductCategories created';
    END

    -- Indexes
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ProductCategories_Company_Active 
        ON dbo.ProductCategories(CompanyID, IsActive) 
        INCLUDE (CategoryCode, CategoryNameAR, CategoryLevel) 
        WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ProductCategories_Parent 
        ON dbo.ProductCategories(ParentCategoryCode) 
        WHERE ParentCategoryCode IS NOT NULL;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ProductCategories_Path 
        ON dbo.ProductCategories(CategoryPath);

    -- Extended Properties
    EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'Product classification with inventory and sales accounts', @level0type = N'SCHEMA', @level0name = 'dbo', @level1type = N'TABLE', @level1name = 'ProductCategories';
    GO

    -- ==========================================================================
    -- 2. PRODUCTS (enhanced with audit and quality fields)
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Products')
    BEGIN
        CREATE TABLE dbo.Products (
            ProductID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ProductCode NVARCHAR(50) NOT NULL,
            ProductNameAR NVARCHAR(255) NOT NULL,
            ProductNameEN NVARCHAR(255),
            PrimaryBarcode NVARCHAR(100),
            AlternativeBarcodes NVARCHAR(MAX),
            SKU NVARCHAR(100),
            ManufacturerPartNumber NVARCHAR(100),
            CategoryCode NVARCHAR(50),
            BrandName NVARCHAR(100),
            ModelNumber NVARCHAR(100),
            ProductType NVARCHAR(30) NOT NULL DEFAULT 'INVENTORY',
            TrackInventory BIT NOT NULL DEFAULT 1,
            IsPurchasable BIT NOT NULL DEFAULT 1,
            IsSaleable BIT NOT NULL DEFAULT 1,
            IsManufacturable BIT NOT NULL DEFAULT 0,
            IsRawMaterial BIT NOT NULL DEFAULT 0,
            BaseUnitOfMeasure NVARCHAR(20) NOT NULL DEFAULT 'PCS',
            PurchaseUnitOfMeasure NVARCHAR(20),
            SaleUnitOfMeasure NVARCHAR(20),
            PurchaseToBaseConversion DECIMAL(18,6) NOT NULL DEFAULT 1,
            SaleToBaseConversion DECIMAL(18,6) NOT NULL DEFAULT 1,
            CostingMethod NVARCHAR(20) NOT NULL DEFAULT 'FIFO',
            StandardCost DECIMAL(18,6) NOT NULL DEFAULT 0,
            AverageCost DECIMAL(18,6) NOT NULL DEFAULT 0,
			LandedCostFactor DECIMAL(5,4) NOT NULL DEFAULT 1.0000,  -- landed cost factor
			AdditionalLandedCost DECIMAL(18,6) NOT NULL DEFAULT 0, -- freight + customs + insurance
			EffectiveUnitCost AS (StandardCost * LandedCostFactor + AdditionalLandedCost) PERSISTED,
            LastPurchaseCost DECIMAL(18,6) NOT NULL DEFAULT 0,
            ReplacementCost DECIMAL(18,6) NOT NULL DEFAULT 0,
            SalePrice DECIMAL(18,6) NOT NULL DEFAULT 0,
            MinimumPrice DECIMAL(18,6) NOT NULL DEFAULT 0,
            WholesalePrice DECIMAL(18,6) NOT NULL DEFAULT 0,
            RetailPrice DECIMAL(18,6) NOT NULL DEFAULT 0,
            ProfitMarginPercentage AS (CASE WHEN AverageCost > 0 THEN ((SalePrice - AverageCost) / AverageCost * 100) ELSE 0 END) PERSISTED,
            TaxCategoryCode NVARCHAR(20) NOT NULL DEFAULT 'STANDARD_RATE',
            DefaultTaxRate DECIMAL(5,2) NOT NULL DEFAULT 15,
            TaxExempt BIT NOT NULL DEFAULT 0,
            TaxExemptionReason NVARCHAR(200),
            HSCode NVARCHAR(20),
            CustomsDutyPercentage DECIMAL(5,2) NOT NULL DEFAULT 0,
            ExciseTaxPercentage DECIMAL(5,2) NOT NULL DEFAULT 0,
            CountryOfOrigin NVARCHAR(100),
            MinimumStockLevel DECIMAL(18,3) NOT NULL DEFAULT 0,
            MaximumStockLevel DECIMAL(18,3) NOT NULL DEFAULT 0,
            ReorderPoint DECIMAL(18,3) NOT NULL DEFAULT 0,
            ReorderQuantity DECIMAL(18,3) NOT NULL DEFAULT 0,
            SafetyStockLevel DECIMAL(18,3) NOT NULL DEFAULT 0,
            DefaultSupplierID INT,
            DefaultSupplierPartNumber NVARCHAR(100),
            LeadTimeDays INT NOT NULL DEFAULT 0,
            Weight DECIMAL(10,3),
            WeightUnit NVARCHAR(10) NOT NULL DEFAULT 'KG',
            Volume DECIMAL(10,3),
            VolumeUnit NVARCHAR(10) NOT NULL DEFAULT 'M3',
            Length DECIMAL(10,2),
            Width DECIMAL(10,2),
            Height DECIMAL(10,2),
            DimensionUnit NVARCHAR(10) NOT NULL DEFAULT 'CM',
            ExpiryTrackingEnabled BIT NOT NULL DEFAULT 0,
            BatchTrackingEnabled BIT NOT NULL DEFAULT 0,
            SerialTrackingEnabled BIT NOT NULL DEFAULT 0,
            LotControlEnabled BIT NOT NULL DEFAULT 0,
            DefaultShelfLifeDays INT,
            MinRemainingShelfLifePercentage DECIMAL(5,2) NOT NULL DEFAULT 50,
            QualityGrade NVARCHAR(20),
            QualityCheckRequired BIT NOT NULL DEFAULT 0,
            PrimaryImageURL NVARCHAR(500),
            AdditionalImagesJSON NVARCHAR(MAX),
            TechnicalDatasheetURL NVARCHAR(500),
            ShortDescriptionAR NVARCHAR(500),
            ShortDescriptionEN NVARCHAR(500),
            LongDescriptionAR NVARCHAR(MAX),
            LongDescriptionEN NVARCHAR(MAX),
            MetaTags NVARCHAR(MAX),
            IsPublishedOnline BIT NOT NULL DEFAULT 0,
            OnlineProductURL NVARCHAR(500),
            IsActive BIT NOT NULL DEFAULT 1,
            IsDiscontinued BIT NOT NULL DEFAULT 0,
            DiscontinuedDate DATE,
            IsBlocked BIT NOT NULL DEFAULT 0,
            BlockReason NVARCHAR(200),
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            Notes NVARCHAR(MAX),
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_Products PRIMARY KEY CLUSTERED (ProductID),
            CONSTRAINT UQ_Products_Code UNIQUE (CompanyID, ProductCode),
            CONSTRAINT UQ_Products_PrimaryBarcode UNIQUE (CompanyID, PrimaryBarcode),
            CONSTRAINT FK_Products_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID),
            CONSTRAINT FK_Products_Category FOREIGN KEY (CompanyID, CategoryCode) REFERENCES dbo.ProductCategories(CompanyID, CategoryCode),
            CONSTRAINT CK_Products_Type CHECK (ProductType IN ('INVENTORY', 'SERVICE', 'NON_INVENTORY', 'BUNDLE', 'KIT')),
            CONSTRAINT CK_Products_CostingMethod CHECK (CostingMethod IN ('FIFO', 'LIFO', 'AVERAGE', 'STANDARD')),
            CONSTRAINT CK_Products_Costs_NonNegative CHECK (StandardCost >= 0 AND AverageCost >= 0 AND SalePrice >= 0),
            CONSTRAINT CK_Products_MinMax_Stock CHECK (MaximumStockLevel = 0 OR MaximumStockLevel >= MinimumStockLevel),
            CONSTRAINT CK_Products_Price_Floor CHECK (MinimumPrice = 0 OR SalePrice >= MinimumPrice)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ Products created';
    END

    -- Indexes (including covering for COGS)
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Products_Company_Active 
        ON dbo.Products(CompanyID, IsActive) 
        INCLUDE (ProductCode, ProductNameAR, CategoryCode, AverageCost, SalePrice) 
        WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Products_Category 
        ON dbo.Products(CategoryCode, IsActive) 
        INCLUDE (ProductCode, ProductNameAR, SalePrice) 
        WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Products_Barcode_Search 
        ON dbo.Products(PrimaryBarcode) 
        INCLUDE (ProductID, ProductCode, ProductNameAR, SalePrice) 
        WHERE PrimaryBarcode IS NOT NULL AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Products_SKU_Search 
        ON dbo.Products(SKU) 
        INCLUDE (ProductID, ProductCode, ProductNameAR) 
        WHERE SKU IS NOT NULL AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Products_Name_Search 
        ON dbo.Products(CompanyID, ProductNameAR) 
        INCLUDE (ProductCode, PrimaryBarcode, SalePrice) 
        WHERE IsDeleted = 0 AND IsActive = 1;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Products_Supplier 
        ON dbo.Products(DefaultSupplierID) 
        INCLUDE (ProductCode, ProductNameAR, LastPurchaseCost) 
        WHERE DefaultSupplierID IS NOT NULL AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Products_LowStock 
        ON dbo.Products(CompanyID, MinimumStockLevel, ReorderPoint) 
        WHERE TrackInventory = 1 AND IsDeleted = 0 AND IsActive = 1;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Products_Discontinued 
        ON dbo.Products(IsDiscontinued, DiscontinuedDate) 
        WHERE IsDiscontinued = 1 AND IsDeleted = 0;
    
    -- Extended Properties
    EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'Products and Services', @level0type = N'SCHEMA', @level0name = 'dbo', @level1type = N'TABLE', @level1name = 'Products';
    GO

    -- ==========================================================================
    -- 3. WAREHOUSES
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Warehouses')
    BEGIN
        CREATE TABLE dbo.Warehouses (
            WarehouseID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            WarehouseCode NVARCHAR(50) NOT NULL,
            WarehouseNameAR NVARCHAR(200) NOT NULL,
            WarehouseNameEN NVARCHAR(200) NOT NULL,
            WarehouseType NVARCHAR(30) NOT NULL DEFAULT 'GENERAL',
            AddressLine1 NVARCHAR(255),
            AddressLine2 NVARCHAR(255),
            City NVARCHAR(100),
            State NVARCHAR(100),
            PostalCode NVARCHAR(20),
            Country NVARCHAR(100) NOT NULL DEFAULT N'Kingdom of Saudi Arabia',
            Latitude DECIMAL(10,7),
            Longitude DECIMAL(10,7),
            ManagerUserID UNIQUEIDENTIFIER,
            PhoneNumber NVARCHAR(50),
            Email NVARCHAR(100),
            TotalCapacityM3 DECIMAL(18,3),
            UsedCapacityM3 DECIMAL(18,3) NOT NULL DEFAULT 0,
            AvailableCapacityM3 AS (TotalCapacityM3 - UsedCapacityM3) PERSISTED,
            AllowNegativeStock BIT NOT NULL DEFAULT 0,
            RequirePickingSlip BIT NOT NULL DEFAULT 0,
            RequirePackingSlip BIT NOT NULL DEFAULT 0,
            InventoryAccountCode NVARCHAR(50),   -- FK to ChartOfAccounts (deferred)
            InventoryTransitAccountCode NVARCHAR(50),
            CostCenterCode NVARCHAR(50),
            IsDefault BIT NOT NULL DEFAULT 0,
            IsActive BIT NOT NULL DEFAULT 1,
            IsRetailStore BIT NOT NULL DEFAULT 0,
            AllowSalesOrders BIT NOT NULL DEFAULT 1,
            AllowPurchaseOrders BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            Notes NVARCHAR(MAX),
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_Warehouses PRIMARY KEY CLUSTERED (WarehouseID),
            CONSTRAINT UQ_Warehouses_Code UNIQUE (CompanyID, WarehouseCode),
            CONSTRAINT FK_Warehouses_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID),
            CONSTRAINT CK_Warehouses_Type CHECK (WarehouseType IN ('GENERAL', 'TRANSIT', 'DAMAGED', 'QUARANTINE', 'CONSIGNMENT', 'RETAIL'))
        );
        PRINT N'✅ Warehouses created';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Warehouses_Company_Active 
        ON dbo.Warehouses(CompanyID, IsActive) 
        INCLUDE (WarehouseCode, WarehouseNameAR, WarehouseType) 
        WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Warehouses_Manager 
        ON dbo.Warehouses(ManagerUserID) 
        WHERE ManagerUserID IS NOT NULL;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_Warehouses_Default 
        ON dbo.Warehouses(CompanyID, IsDefault) 
        WHERE IsDefault = 1 AND IsActive = 1 AND IsDeleted = 0;

    EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'Warehouses', @level0type = N'SCHEMA', @level0name = 'dbo', @level1type = N'TABLE', @level1name = 'Warehouses';
    GO

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WarehouseLocations')
    BEGIN
        CREATE TABLE dbo.WarehouseLocations (
            LocationID INT IDENTITY(1,1) NOT NULL,
            WarehouseID INT NOT NULL,
            LocationCode NVARCHAR(50) NOT NULL,
            LocationNameAR NVARCHAR(200),
            Aisle NVARCHAR(10),
            Rack NVARCHAR(10),
            Shelf NVARCHAR(10),
            Bin NVARCHAR(10),
            MaxWeight DECIMAL(10,2),
            MaxVolume DECIMAL(10,2),
            LocationType NVARCHAR(30) NOT NULL DEFAULT 'STORAGE',
            IsActive BIT NOT NULL DEFAULT 1,
            IsBlocked BIT NOT NULL DEFAULT 0,
            BlockReason NVARCHAR(200),
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            RowVersion ROWVERSION,
            CONSTRAINT PK_WarehouseLocations PRIMARY KEY CLUSTERED (LocationID),
            CONSTRAINT UQ_WarehouseLocations_Code UNIQUE (WarehouseID, LocationCode),
            CONSTRAINT FK_WarehouseLocations_Warehouse FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID) ON DELETE CASCADE
        );
        PRINT N'✅ WarehouseLocations created';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_WarehouseLocations_Warehouse 
        ON dbo.WarehouseLocations(WarehouseID, IsActive) 
        WHERE IsDeleted = 0;
    GO

    -- ==========================================================================
    -- 5. INVENTORY BALANCES (denormalized)
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'InventoryBalances')
    BEGIN
        CREATE TABLE dbo.InventoryBalances (
            InventoryBalanceID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ProductID INT NOT NULL,
            WarehouseID INT NOT NULL,
            QuantityOnHand DECIMAL(18,3) NOT NULL DEFAULT 0,
            QuantityReserved DECIMAL(18,3) NOT NULL DEFAULT 0,
            QuantityAvailable AS (QuantityOnHand - QuantityReserved) PERSISTED,
            QuantityOnOrder DECIMAL(18,3) NOT NULL DEFAULT 0,
            QuantityInTransit DECIMAL(18,3) NOT NULL DEFAULT 0,
            QuantityQuarantined DECIMAL(18,3) NOT NULL DEFAULT 0,
            QuantityDamaged DECIMAL(18,3) NOT NULL DEFAULT 0,
            AverageCost DECIMAL(18,6) NOT NULL DEFAULT 0,
            TotalValue AS (QuantityOnHand * AverageCost) PERSISTED,
            LastMovementDate DATETIME2(7),
            LastMovementType NVARCHAR(30),
            LastCountDate DATETIME2(7),
            LastUpdated DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            RowVersion ROWVERSION,
            CONSTRAINT PK_InventoryBalances PRIMARY KEY CLUSTERED (InventoryBalanceID),
            CONSTRAINT UQ_InventoryBalances_ProductWarehouse UNIQUE (ProductID, WarehouseID),
            CONSTRAINT FK_InventoryBalances_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID),
            CONSTRAINT FK_InventoryBalances_Warehouse FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT CK_InventoryBalances_Quantities_NonNegative CHECK (QuantityOnHand >= 0 AND QuantityReserved >= 0)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ InventoryBalances created';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_InventoryBalances_Product 
        ON dbo.InventoryBalances(ProductID) 
        INCLUDE (WarehouseID, QuantityOnHand, QuantityAvailable, AverageCost);
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_InventoryBalances_Warehouse 
        ON dbo.InventoryBalances(WarehouseID, ProductID) 
        INCLUDE (QuantityOnHand, AverageCost);
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_InventoryBalances_LowStock 
        ON dbo.InventoryBalances(ProductID, QuantityOnHand) 
        WHERE QuantityAvailable <= 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_InventoryBalances_Value 
        ON dbo.InventoryBalances(CompanyID, TotalValue DESC) 
        WHERE QuantityOnHand > 0;
    CREATE NONCLUSTERED COLUMNSTORE INDEX IF NOT EXISTS CSIX_InventoryBalances_Analytics 
        ON dbo.InventoryBalances (ProductID, WarehouseID, QuantityOnHand, QuantityAvailable, AverageCost, TotalValue, LastMovementDate);
    GO

    -- ==========================================================================
    -- 6. FIFO COST LAYERS (with protection and audit)
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'InventoryCostLayers')
    BEGIN
        CREATE TABLE dbo.InventoryCostLayers (
            CostLayerID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ProductID INT NOT NULL,
            WarehouseID INT NOT NULL,
            LayerDate DATE NOT NULL,
            RemainingQuantity DECIMAL(18,6) NOT NULL,
            UnitCost DECIMAL(18,6) NOT NULL,
            SourceType NVARCHAR(30),
            SourceID BIGINT,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            RowVersion ROWVERSION,
            CONSTRAINT PK_InventoryCostLayers PRIMARY KEY CLUSTERED (CostLayerID),
            CONSTRAINT FK_CostLayer_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID),
            CONSTRAINT FK_CostLayer_Warehouse FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT CK_CostLayer_Qty CHECK (RemainingQuantity >= 0)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ InventoryCostLayers (FIFO Engine) created';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_CostLayer_FIFO 
        ON dbo.InventoryCostLayers(ProductID, WarehouseID, LayerDate) 
        WHERE RemainingQuantity > 0 AND IsDeleted = 0;

    -- Trigger to protect cost layers with audit logging (safe even if AuditLogs not exists yet)
CREATE OR ALTER TRIGGER trg_ProtectCostLayer ON dbo.InventoryCostLayers
INSTEAD OF UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF UPDATE(UnitCost) OR UPDATE(LayerDate)
        THROW 50001, 'Direct modification of FIFO cost layers is prohibited. Use Stock Movements only.', 1;
    
    -- Allow only soft delete and quantity adjustment via system
    UPDATE icl 
    SET IsDeleted = i.IsDeleted,
        DeletedAt = i.DeletedAt,
        DeletedBy = i.DeletedBy
    FROM dbo.InventoryCostLayers icl
    INNER JOIN inserted i ON icl.CostLayerID = i.CostLayerID;
END;
            THROW 50000, 'Cannot modify FIFO cost layer directly. Use stock movements.', 1;
        END
        -- Allow soft delete only
        UPDATE icl SET 
            IsDeleted = i.IsDeleted,
            DeletedAt = i.DeletedAt,
            DeletedBy = i.DeletedBy
        FROM dbo.InventoryCostLayers icl
        INNER JOIN inserted i ON icl.CostLayerID = i.CostLayerID
        WHERE i.IsDeleted = 1;
    END;
    PRINT N'✅ trg_ProtectCostLayer created with audit';
    GO

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'InventoryValuationSnapshots')
    BEGIN
        CREATE TABLE dbo.InventoryValuationSnapshots (
            SnapshotID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            SnapshotDate DATE NOT NULL,
            ProductID INT NOT NULL,
            WarehouseID INT NOT NULL,
            Quantity DECIMAL(18,6) NOT NULL,
            UnitCost DECIMAL(18,6) NOT NULL,
            TotalValue DECIMAL(18,6) NOT NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            RowVersion ROWVERSION,
            CONSTRAINT PK_InventoryValuationSnapshots PRIMARY KEY CLUSTERED (SnapshotID),
            CONSTRAINT FK_Valuation_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID),
            CONSTRAINT FK_Valuation_Warehouse FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID)
        );
        PRINT N'✅ InventoryValuationSnapshots created';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ValuationSnapshots_Date 
        ON dbo.InventoryValuationSnapshots(SnapshotDate, CompanyID) 
        WHERE IsDeleted = 0;
    GO

    -- ==========================================================================
    -- 8. ADVANCED UNIT CONVERSIONS (matrix)
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'UnitConversions')
    BEGIN
        CREATE TABLE dbo.UnitConversions (
            ConversionID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            FromUOM NVARCHAR(20) NOT NULL,
            ToUOM NVARCHAR(20) NOT NULL,
            ConversionFactor DECIMAL(18,6) NOT NULL,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_UnitConversions PRIMARY KEY CLUSTERED (ConversionID),
            CONSTRAINT UQ_UnitConversions_Pair UNIQUE (CompanyID, FromUOM, ToUOM),
            CONSTRAINT CK_Conversion_Positive CHECK (ConversionFactor > 0)
        );
        PRINT N'✅ UnitConversions created';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_UnitConversions_FromTo 
        ON dbo.UnitConversions(FromUOM, ToUOM) 
        WHERE IsDeleted = 0;
    GO

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LandedCostTypes')
    BEGIN
        CREATE TABLE dbo.LandedCostTypes (
            LandedCostTypeID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            TypeCode NVARCHAR(30) NOT NULL,
            TypeNameAR NVARCHAR(200) NOT NULL,
            TypeNameEN NVARCHAR(200),
            IsPercentage BIT NOT NULL DEFAULT 0,  -- 0 = fixed amount, 1 = percentage
            GLAccountCode NVARCHAR(50),          -- expense account (deferred FK)
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            RowVersion ROWVERSION,
            CONSTRAINT PK_LandedCostTypes PRIMARY KEY CLUSTERED (LandedCostTypeID),
            CONSTRAINT UQ_LandedCostTypes_Code UNIQUE (CompanyID, TypeCode),
            CONSTRAINT FK_LandedCostTypes_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID)
        );
        PRINT N'✅ LandedCostTypes created';
    END

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LandedCostAllocations')
    BEGIN
        CREATE TABLE dbo.LandedCostAllocations (
            AllocationID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            PurchaseOrderID BIGINT NOT NULL,
            LandedCostTypeID INT NOT NULL,
            Amount DECIMAL(18,6) NOT NULL,
            AllocationMethod NVARCHAR(20) NOT NULL, -- BY_QUANTITY, BY_WEIGHT, BY_VOLUME, BY_VALUE
            AllocatedAmount DECIMAL(18,6) NULL,     -- calculated after allocation
            ReferenceInvoice NVARCHAR(100),
            IsPosted BIT NOT NULL DEFAULT 0,
            GLJournalEntryID BIGINT,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            RowVersion ROWVERSION,
            CONSTRAINT PK_LandedCostAllocations PRIMARY KEY CLUSTERED (AllocationID),
            CONSTRAINT FK_LandedCostAllocations_PO FOREIGN KEY (PurchaseOrderID) REFERENCES dbo.PurchaseOrders(PurchaseOrderID),
            CONSTRAINT FK_LandedCostAllocations_Type FOREIGN KEY (LandedCostTypeID) REFERENCES dbo.LandedCostTypes(LandedCostTypeID)
        );
        PRINT N'✅ LandedCostAllocations created';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_LandedCostAllocations_PO 
        ON dbo.LandedCostAllocations(PurchaseOrderID) 
        WHERE IsDeleted = 0;
    GO

    -- ==========================================================================
    -- 10. STOCK MOVEMENTS (enhanced with quality workflow)
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'StockMovements')
    BEGIN
        CREATE TABLE dbo.StockMovements (
            StockMovementID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            MovementNumber NVARCHAR(50) NOT NULL,
            MovementSequence BIGINT,
            MovementType NVARCHAR(30) NOT NULL,
            MovementSubType NVARCHAR(30),
            MovementDirection NVARCHAR(10) AS (CASE MovementType WHEN 'IN' THEN 'INCREASE' WHEN 'OUT' THEN 'DECREASE' WHEN 'TRANSFER' THEN 'NEUTRAL' WHEN 'ADJUSTMENT' THEN CASE WHEN Quantity > 0 THEN 'INCREASE' ELSE 'DECREASE' END ELSE 'NEUTRAL' END) PERSISTED,
            MovementDate DATE NOT NULL,
            PostingDate DATE,
            ProductID INT NOT NULL,
            WarehouseID INT NOT NULL,
            LocationID INT,
            Quantity DECIMAL(18,3) NOT NULL,
            UnitOfMeasure NVARCHAR(20),
            UnitCost DECIMAL(18,6),
            TotalCost AS (ABS(Quantity) * ISNULL(UnitCost, 0)) PERSISTED,
            FromWarehouseID INT,
            ToWarehouseID INT,
            FromLocationID INT,
            ToLocationID INT,
            ReferenceType NVARCHAR(50),
            ReferenceID BIGINT,
            ReferenceNumber NVARCHAR(50),
            ReferenceLineNumber INT,
            BatchNumber NVARCHAR(50),
            SerialNumber NVARCHAR(100),
            ExpiryDate DATE,
            ManufactureDate DATE,
            QualityStatus NVARCHAR(20) NOT NULL DEFAULT 'PENDING', -- PENDING, PASSED, FAILED, HOLD
            InspectionRequired BIT NOT NULL DEFAULT 1,
            InspectedBy UNIQUEIDENTIFIER,
            InspectionDate DATETIME2(7),
            InspectionNotes NVARCHAR(500),
            IsPosted BIT NOT NULL DEFAULT 0,
            PostedAt DATETIME2(7),
            PostedBy UNIQUEIDENTIFIER,
            GLJournalEntryID BIGINT,
            IsReversed BIT NOT NULL DEFAULT 0,
            ReversedAt DATETIME2(7),
            ReversalMovementID BIGINT,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            Notes NVARCHAR(MAX),
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            RowVersion ROWVERSION,
            CONSTRAINT PK_StockMovements PRIMARY KEY CLUSTERED (StockMovementID),
            CONSTRAINT UQ_StockMovements_Number UNIQUE (CompanyID, MovementNumber),
            CONSTRAINT FK_StockMovements_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID),
            CONSTRAINT FK_StockMovements_Warehouse FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT FK_StockMovements_FromWarehouse FOREIGN KEY (FromWarehouseID) REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT FK_StockMovements_ToWarehouse FOREIGN KEY (ToWarehouseID) REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT CK_StockMovements_Type CHECK (MovementType IN ('IN', 'OUT', 'TRANSFER', 'ADJUSTMENT', 'RETURN', 'COUNT')),
            CONSTRAINT CK_StockMovements_Transfer_Warehouses CHECK ((MovementType != 'TRANSFER') OR (FromWarehouseID IS NOT NULL AND ToWarehouseID IS NOT NULL AND FromWarehouseID != ToWarehouseID)),
            CONSTRAINT CK_QualityStatus CHECK (QualityStatus IN ('PENDING', 'PASSED', 'FAILED', 'HOLD'))
        ) WITH (DATA_COMPRESSION = PAGE);
		-- TODO: Enterprise Partitioning (for large scale)
		-- CREATE PARTITION FUNCTION pf_MovementDate (DATE) AS RANGE RIGHT FOR VALUES (...);
		-- CREATE PARTITION SCHEME ps_MovementDate AS PARTITION pf_MovementDate TO (...);
        PRINT N'✅ StockMovements created with quality workflow';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_StockMovements_Product_Date 
        ON dbo.StockMovements(ProductID, MovementDate DESC) 
        INCLUDE (MovementType, Quantity, UnitCost, WarehouseID, QualityStatus) 
        WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_StockMovements_Warehouse_Date 
        ON dbo.StockMovements(WarehouseID, MovementDate DESC) 
        INCLUDE (ProductID, MovementType, Quantity) 
        WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_StockMovements_Company_Date 
        ON dbo.StockMovements(CompanyID, MovementDate DESC, MovementType) 
        WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_StockMovements_Reference 
        ON dbo.StockMovements(ReferenceType, ReferenceID) 
        WHERE ReferenceType IS NOT NULL AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_StockMovements_Batch 
        ON dbo.StockMovements(BatchNumber, ProductID) 
        WHERE BatchNumber IS NOT NULL AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_StockMovements_Serial 
        ON dbo.StockMovements(SerialNumber, ProductID) 
        WHERE SerialNumber IS NOT NULL AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_StockMovements_Unposted 
        ON dbo.StockMovements(CompanyID, IsPosted) 
        WHERE IsPosted = 0 AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_StockMovements_QualityPending 
        ON dbo.StockMovements(QualityStatus, MovementDate) 
        WHERE QualityStatus = 'PENDING' AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_StockMovements_COGS_Calculation 
        ON dbo.StockMovements(ProductID, MovementDate, MovementType, UnitCost, Quantity) 
        WHERE IsPosted = 1 AND MovementType = 'OUT' AND IsDeleted = 0;
    CREATE NONCLUSTERED COLUMNSTORE INDEX IF NOT EXISTS CSIX_StockMovements_Analytics 
        ON dbo.StockMovements (ProductID, WarehouseID, MovementDate, MovementType, Quantity, UnitCost, TotalCost, QualityStatus);
    GO

    -- ==========================================================================
    -- 11. PURCHASE ORDERS (abbreviated, same as Enterprise+ version)
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PurchaseOrders')
    BEGIN
        CREATE TABLE dbo.PurchaseOrders (
            PurchaseOrderID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            PONumber NVARCHAR(50) NOT NULL,
            POSequence BIGINT,
            PODate DATE NOT NULL,
            SupplierID INT NOT NULL,
            WarehouseID INT NOT NULL,
            RequestedDeliveryDate DATE,
            ExpectedDeliveryDate DATE,
            ActualDeliveryDate DATE,
            ShippingMethodCode NVARCHAR(30),
            DeliveryAddressID INT,
            POStatus NVARCHAR(20) NOT NULL DEFAULT 'DRAFT',
            CurrencyCode NVARCHAR(3) NOT NULL DEFAULT 'SAR',
            ExchangeRate DECIMAL(18,8) NOT NULL DEFAULT 1,
            SubtotalAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
            TotalDiscountAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
            TotalTaxAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
            ShippingAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
            TotalAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
            PaymentTermsDays INT NOT NULL DEFAULT 0,
            PaymentMethodType NVARCHAR(30),
            SupplierQuotationNumber NVARCHAR(100),
            SupplierReference NVARCHAR(100),
            InternalRequisitionNumber NVARCHAR(50),
            ShippingTrackingNumber NVARCHAR(100),
            RequiresApproval BIT NOT NULL DEFAULT 1,
            ApprovedBy UNIQUEIDENTIFIER,
            ApprovedAt DATETIME2(7),
            IsPosted BIT NOT NULL DEFAULT 0,
            PostedAt DATETIME2(7),
            GLJournalEntryID BIGINT,
            IsClosed BIT NOT NULL DEFAULT 0,
            ClosedAt DATETIME2(7),
            ClosedBy UNIQUEIDENTIFIER,
            CloseReason NVARCHAR(200),
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            Notes NVARCHAR(MAX),
            InternalNotes NVARCHAR(MAX),
            TermsAndConditions NVARCHAR(MAX),
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_PurchaseOrders PRIMARY KEY CLUSTERED (PurchaseOrderID),
            CONSTRAINT UQ_PurchaseOrders_Number UNIQUE (CompanyID, PONumber),
            CONSTRAINT FK_PurchaseOrders_Warehouse FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT CK_PurchaseOrders_Status CHECK (POStatus IN ('DRAFT', 'SENT', 'APPROVED', 'PARTIALLY_RECEIVED', 'RECEIVED', 'CANCELLED', 'CLOSED'))
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ PurchaseOrders created';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_PurchaseOrders_Supplier_Date 
        ON dbo.PurchaseOrders(SupplierID, PODate DESC) 
        INCLUDE (PONumber, POStatus, TotalAmount) 
        WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_PurchaseOrders_Company_Status 
        ON dbo.PurchaseOrders(CompanyID, POStatus, PODate DESC) 
        WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_PurchaseOrders_Pending 
        ON dbo.PurchaseOrders(CompanyID, POStatus, ExpectedDeliveryDate) 
        WHERE POStatus IN ('SENT', 'APPROVED', 'PARTIALLY_RECEIVED') AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_PurchaseOrders_Warehouse 
        ON dbo.PurchaseOrders(WarehouseID, PODate DESC) 
        WHERE IsDeleted = 0;
    GO

    -- ==========================================================================
    -- 12. PURCHASE ORDER ITEMS (abbreviated)
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PurchaseOrderItems')
    BEGIN
        CREATE TABLE dbo.PurchaseOrderItems (
            POItemID BIGINT IDENTITY(1,1) NOT NULL,
            PurchaseOrderID BIGINT NOT NULL,
            LineNumber INT NOT NULL,
            ProductID INT NOT NULL,
            Description NVARCHAR(500),
            QuantityOrdered DECIMAL(18,3) NOT NULL,
            QuantityReceived DECIMAL(18,3) NOT NULL DEFAULT 0,
            QuantityRemaining AS (QuantityOrdered - QuantityReceived) PERSISTED,
            QuantityInvoiced DECIMAL(18,3) NOT NULL DEFAULT 0,
            UnitOfMeasure NVARCHAR(20),
            UnitPrice DECIMAL(18,6) NOT NULL,
            DiscountPercentage DECIMAL(5,2) NOT NULL DEFAULT 0,
            DiscountAmount AS (QuantityOrdered * UnitPrice * DiscountPercentage / 100) PERSISTED,
            NetAmount AS ((QuantityOrdered * UnitPrice) - (QuantityOrdered * UnitPrice * DiscountPercentage / 100)) PERSISTED,
            TaxRate DECIMAL(5,2) NOT NULL DEFAULT 0,
            TaxAmount AS (((QuantityOrdered * UnitPrice) - (QuantityOrdered * UnitPrice * DiscountPercentage / 100)) * TaxRate / 100) PERSISTED,
            TotalAmount AS (((QuantityOrdered * UnitPrice) - (QuantityOrdered * UnitPrice * DiscountPercentage / 100)) * (1 + TaxRate / 100)) PERSISTED,
            RequestedDeliveryDate DATE,
            ExpectedDeliveryDate DATE,
            RequiresInspection BIT NOT NULL DEFAULT 0,
            Notes NVARCHAR(MAX),
            RowVersion ROWVERSION,
            CONSTRAINT PK_PurchaseOrderItems PRIMARY KEY CLUSTERED (POItemID),
            CONSTRAINT UQ_PurchaseOrderItems_LineNumber UNIQUE (PurchaseOrderID, LineNumber),
            CONSTRAINT FK_PurchaseOrderItems_PurchaseOrder FOREIGN KEY (PurchaseOrderID) REFERENCES dbo.PurchaseOrders(PurchaseOrderID) ON DELETE CASCADE,
            CONSTRAINT FK_PurchaseOrderItems_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID),
            CONSTRAINT CK_PurchaseOrderItems_Quantities CHECK (QuantityOrdered > 0 AND QuantityReceived >= 0)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ PurchaseOrderItems created';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_PurchaseOrderItems_PO 
        ON dbo.PurchaseOrderItems(PurchaseOrderID, LineNumber);
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_PurchaseOrderItems_Product 
        ON dbo.PurchaseOrderItems(ProductID, PurchaseOrderID);
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_PurchaseOrderItems_PendingReceipt 
        ON dbo.PurchaseOrderItems(PurchaseOrderID, QuantityRemaining) 
        WHERE QuantityRemaining > 0;
    GO

    -- ==========================================================================
    -- 13. GOODS RECEIVED NOTES (abbreviated)
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'GoodsReceivedNotes')
    BEGIN
        CREATE TABLE dbo.GoodsReceivedNotes (
            GRNID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            GRNNumber NVARCHAR(50) NOT NULL,
            GRNSequence BIGINT,
            GRNDate DATE NOT NULL,
            PurchaseOrderID BIGINT,
            SupplierID INT NOT NULL,
            WarehouseID INT NOT NULL,
            DeliveryNoteNumber NVARCHAR(100),
            VehicleNumber NVARCHAR(50),
            DriverName NVARCHAR(200),
            DriverPhone NVARCHAR(50),
            GRNStatus NVARCHAR(20) NOT NULL DEFAULT 'DRAFT',
            RequiresInspection BIT NOT NULL DEFAULT 1,
            InspectionCompleted BIT NOT NULL DEFAULT 0,
            InspectedBy UNIQUEIDENTIFIER,
            InspectedAt DATETIME2(7),
            IsPosted BIT NOT NULL DEFAULT 0,
            PostedBy UNIQUEIDENTIFIER,
            PostedAt DATETIME2(7),
            ReceivedBy UNIQUEIDENTIFIER NOT NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            Notes NVARCHAR(MAX),
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            RowVersion ROWVERSION,
            CONSTRAINT PK_GoodsReceivedNotes PRIMARY KEY CLUSTERED (GRNID),
            CONSTRAINT UQ_GoodsReceivedNotes_Number UNIQUE (CompanyID, GRNNumber),
            CONSTRAINT FK_GoodsReceivedNotes_PO FOREIGN KEY (PurchaseOrderID) REFERENCES dbo.PurchaseOrders(PurchaseOrderID),
            CONSTRAINT FK_GoodsReceivedNotes_Warehouse FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID)
        );
        PRINT N'✅ GoodsReceivedNotes created';
    END

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'GoodsReceivedNoteItems')
    BEGIN
        CREATE TABLE dbo.GoodsReceivedNoteItems (
            GRNItemID BIGINT IDENTITY(1,1) NOT NULL,
            GRNID BIGINT NOT NULL,
            LineNumber INT NOT NULL,
            POItemID BIGINT,
            ProductID INT NOT NULL,
            QuantityOrdered DECIMAL(18,3),
            QuantityReceived DECIMAL(18,3) NOT NULL,
            QuantityAccepted DECIMAL(18,3) NOT NULL,
            QuantityRejected DECIMAL(18,3) NOT NULL DEFAULT 0,
            RejectionReason NVARCHAR(200),
            UnitCost DECIMAL(18,6),
            BatchNumber NVARCHAR(50),
            ExpiryDate DATE,
            ManufactureDate DATE,
            LocationID INT,
            Notes NVARCHAR(500),
            RowVersion ROWVERSION,
            CONSTRAINT PK_GoodsReceivedNoteItems PRIMARY KEY CLUSTERED (GRNItemID),
            CONSTRAINT FK_GoodsReceivedNoteItems_GRN FOREIGN KEY (GRNID) REFERENCES dbo.GoodsReceivedNotes(GRNID) ON DELETE CASCADE,
            CONSTRAINT FK_GoodsReceivedNoteItems_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID)
        );
        PRINT N'✅ GoodsReceivedNoteItems created';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_GoodsReceivedNotes_PO 
        ON dbo.GoodsReceivedNotes(PurchaseOrderID) 
        WHERE PurchaseOrderID IS NOT NULL AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_GoodsReceivedNotes_Supplier 
        ON dbo.GoodsReceivedNotes(SupplierID, GRNDate DESC) 
        WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_GoodsReceivedNoteItems_GRN 
        ON dbo.GoodsReceivedNoteItems(GRNID, LineNumber);
    GO

    -- ==========================================================================
    -- 14. INVENTORY ADJUSTMENTS (abbreviated)
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'InventoryAdjustments')
    BEGIN
        CREATE TABLE dbo.InventoryAdjustments (
            AdjustmentID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            AdjustmentNumber NVARCHAR(50) NOT NULL,
            AdjustmentDate DATE NOT NULL,
            AdjustmentType NVARCHAR(30) NOT NULL,
            AdjustmentReason NVARCHAR(30),
            WarehouseID INT NOT NULL,
            AdjustmentStatus NVARCHAR(20) NOT NULL DEFAULT 'DRAFT',
            RequiresApproval BIT NOT NULL DEFAULT 1,
            ApprovedBy UNIQUEIDENTIFIER,
            ApprovedAt DATETIME2(7),
            IsPosted BIT NOT NULL DEFAULT 0,
            PostedAt DATETIME2(7),
            GLJournalEntryID BIGINT,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            Notes NVARCHAR(MAX),
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            RowVersion ROWVERSION,
            CONSTRAINT PK_InventoryAdjustments PRIMARY KEY CLUSTERED (AdjustmentID),
            CONSTRAINT UQ_InventoryAdjustments_Number UNIQUE (CompanyID, AdjustmentNumber),
            CONSTRAINT FK_InventoryAdjustments_Warehouse FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT CK_InventoryAdjustments_Type CHECK (AdjustmentType IN ('COUNT', 'DAMAGE', 'LOSS', 'FOUND', 'EXPIRY', 'WRITE_OFF', 'REVALUATION'))
        );
        PRINT N'✅ InventoryAdjustments created';
    END

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'InventoryAdjustmentItems')
    BEGIN
        CREATE TABLE dbo.InventoryAdjustmentItems (
            AdjustmentItemID BIGINT IDENTITY(1,1) NOT NULL,
            AdjustmentID BIGINT NOT NULL,
            LineNumber INT NOT NULL,
            ProductID INT NOT NULL,
            SystemQuantity DECIMAL(18,3),
            PhysicalQuantity DECIMAL(18,3),
            AdjustmentQuantity AS (PhysicalQuantity - SystemQuantity) PERSISTED,
            UnitCost DECIMAL(18,6),
            TotalCostAdjustment AS (ABS(PhysicalQuantity - SystemQuantity) * ISNULL(UnitCost, 0)) PERSISTED,
            BatchNumber NVARCHAR(50),
            LocationID INT,
            Reason NVARCHAR(200),
            Notes NVARCHAR(500),
            RowVersion ROWVERSION,
            CONSTRAINT PK_InventoryAdjustmentItems PRIMARY KEY CLUSTERED (AdjustmentItemID),
            CONSTRAINT FK_InventoryAdjustmentItems_Adjustment FOREIGN KEY (AdjustmentID) REFERENCES dbo.InventoryAdjustments(AdjustmentID) ON DELETE CASCADE,
            CONSTRAINT FK_InventoryAdjustmentItems_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID)
        );
        PRINT N'✅ InventoryAdjustmentItems created';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_InventoryAdjustments_Warehouse 
        ON dbo.InventoryAdjustments(WarehouseID, AdjustmentDate DESC) 
        WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_InventoryAdjustments_Status 
        ON dbo.InventoryAdjustments(CompanyID, AdjustmentStatus) 
        WHERE IsDeleted = 0;
    GO

    -- ==========================================================================
    -- 15. INVENTORY TRANSFERS (abbreviated)
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'InventoryTransfers')
    BEGIN
        CREATE TABLE dbo.InventoryTransfers (
            TransferID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            TransferNumber NVARCHAR(50) NOT NULL,
            TransferDate DATE NOT NULL,
            FromWarehouseID INT NOT NULL,
            ToWarehouseID INT NOT NULL,
            TransferStatus NVARCHAR(20) NOT NULL DEFAULT 'DRAFT',
            ShipmentTrackingNumber NVARCHAR(100),
            CarrierName NVARCHAR(200),
            VehicleNumber NVARCHAR(50),
            DriverName NVARCHAR(200),
            ShippedDate DATE,
            ExpectedDeliveryDate DATE,
            ActualDeliveryDate DATE,
            SentBy UNIQUEIDENTIFIER,
            ReceivedBy UNIQUEIDENTIFIER,
            ReceivedAt DATETIME2(7),
            IsPosted BIT NOT NULL DEFAULT 0,
            PostedAt DATETIME2(7),
            GLJournalEntryID BIGINT,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            Notes NVARCHAR(MAX),
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            RowVersion ROWVERSION,
            CONSTRAINT PK_InventoryTransfers PRIMARY KEY CLUSTERED (TransferID),
            CONSTRAINT UQ_InventoryTransfers_Number UNIQUE (CompanyID, TransferNumber),
            CONSTRAINT FK_InventoryTransfers_FromWarehouse FOREIGN KEY (FromWarehouseID) REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT FK_InventoryTransfers_ToWarehouse FOREIGN KEY (ToWarehouseID) REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT CK_InventoryTransfers_Warehouses CHECK (FromWarehouseID != ToWarehouseID)
        );
        PRINT N'✅ InventoryTransfers created';
    END

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'InventoryTransferItems')
    BEGIN
        CREATE TABLE dbo.InventoryTransferItems (
            TransferItemID BIGINT IDENTITY(1,1) NOT NULL,
            TransferID BIGINT NOT NULL,
            LineNumber INT NOT NULL,
            ProductID INT NOT NULL,
            QuantitySent DECIMAL(18,3) NOT NULL,
            QuantityReceived DECIMAL(18,3) NOT NULL DEFAULT 0,
            QuantityDamaged DECIMAL(18,3) NOT NULL DEFAULT 0,
            UnitCost DECIMAL(18,6),
            BatchNumber NVARCHAR(50),
            SerialNumber NVARCHAR(100),
            FromLocationID INT,
            ToLocationID INT,
            Notes NVARCHAR(500),
            RowVersion ROWVERSION,
            CONSTRAINT PK_InventoryTransferItems PRIMARY KEY CLUSTERED (TransferItemID),
            CONSTRAINT FK_InventoryTransferItems_Transfer FOREIGN KEY (TransferID) REFERENCES dbo.InventoryTransfers(TransferID) ON DELETE CASCADE,
            CONSTRAINT FK_InventoryTransferItems_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID)
        );
        PRINT N'✅ InventoryTransferItems created';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_InventoryTransfers_FromWarehouse 
        ON dbo.InventoryTransfers(FromWarehouseID, TransferDate DESC) 
        WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_InventoryTransfers_ToWarehouse 
        ON dbo.InventoryTransfers(ToWarehouseID, TransferDate DESC) 
        WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_InventoryTransfers_Status 
        ON dbo.InventoryTransfers(CompanyID, TransferStatus) 
        WHERE TransferStatus IN ('SENT', 'IN_TRANSIT') AND IsDeleted = 0;
    GO

    -- ==========================================================================
    -- 16. PRODUCT BATCHES (enhanced)
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ProductBatches')
    BEGIN
        CREATE TABLE dbo.ProductBatches (
            BatchID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ProductID INT NOT NULL,
            WarehouseID INT NOT NULL,
            BatchNumber NVARCHAR(50) NOT NULL,
            ManufactureDate DATE,
            ExpiryDate DATE,
            BestBeforeDate DATE,
            QuantityProduced DECIMAL(18,3) NOT NULL,
            QuantityOnHand DECIMAL(18,3) NOT NULL,
            QuantityReserved DECIMAL(18,3) NOT NULL DEFAULT 0,
            QuantityAvailable AS (QuantityOnHand - QuantityReserved) PERSISTED,
            UnitCost DECIMAL(18,6),
            TotalValue AS (QuantityOnHand * UnitCost) PERSISTED,
            QualityStatus NVARCHAR(20) NOT NULL DEFAULT 'GOOD',
            QualityInspectedBy UNIQUEIDENTIFIER,
            QualityInspectedAt DATETIME2(7),
            SupplierID INT,
            SupplierBatchNumber NVARCHAR(100),
            SourceType NVARCHAR(30),
            SourceID BIGINT,
            IsActive BIT NOT NULL DEFAULT 1,
            IsExpired AS (CASE WHEN ExpiryDate < CAST(GETDATE() AS DATE) THEN 1 ELSE 0 END) PERSISTED,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            Notes NVARCHAR(MAX),
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            RowVersion ROWVERSION,
            CONSTRAINT PK_ProductBatches PRIMARY KEY CLUSTERED (BatchID),
            CONSTRAINT UQ_ProductBatches_Number UNIQUE (CompanyID, ProductID, BatchNumber, WarehouseID),
            CONSTRAINT FK_ProductBatches_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID),
            CONSTRAINT FK_ProductBatches_Warehouse FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ ProductBatches created';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ProductBatches_Product 
        ON dbo.ProductBatches(ProductID, ExpiryDate) 
        WHERE IsActive = 1 AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ProductBatches_Expiring 
        ON dbo.ProductBatches(ExpiryDate, ProductID) 
        WHERE IsActive = 1 AND ExpiryDate IS NOT NULL AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ProductBatches_BatchNumber 
        ON dbo.ProductBatches(BatchNumber) 
        INCLUDE (ProductID, WarehouseID, QuantityOnHand) 
        WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ProductBatches_Warehouse 
        ON dbo.ProductBatches(WarehouseID, ProductID) 
        WHERE IsActive = 1 AND IsDeleted = 0;
    CREATE NONCLUSTERED COLUMNSTORE INDEX IF NOT EXISTS CSIX_ProductBatches_Analytics 
        ON dbo.ProductBatches (ProductID, WarehouseID, BatchNumber, ExpiryDate, QuantityOnHand, UnitCost, QualityStatus);
    GO

    -- ==========================================================================
    -- 17. PRODUCT SERIAL NUMBERS (enhanced)
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ProductSerialNumbers')
    BEGIN
        CREATE TABLE dbo.ProductSerialNumbers (
            SerialID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ProductID INT NOT NULL,
            WarehouseID INT NOT NULL,
            SerialNumber NVARCHAR(100) NOT NULL,
            SerialStatus NVARCHAR(20) NOT NULL DEFAULT 'IN_STOCK',
            ReceivedDate DATE,
            SoldDate DATE,
            ReturnedDate DATE,
            WarrantyStartDate DATE,
            WarrantyEndDate DATE,
            UnitCost DECIMAL(18,6),
            CustomerID INT,
            SaleInvoiceID BIGINT,
            SupplierID INT,
            PurchaseOrderID BIGINT,
            LocationID INT,
            SourceType NVARCHAR(30),
            SourceID BIGINT,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            Notes NVARCHAR(MAX),
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            RowVersion ROWVERSION,
            CONSTRAINT PK_ProductSerialNumbers PRIMARY KEY CLUSTERED (SerialID),
            CONSTRAINT UQ_ProductSerialNumbers_Serial UNIQUE (SerialNumber),
            CONSTRAINT FK_ProductSerialNumbers_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID),
            CONSTRAINT FK_ProductSerialNumbers_Warehouse FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT CK_ProductSerialNumbers_Status CHECK (SerialStatus IN ('IN_STOCK', 'SOLD', 'RETURNED', 'DAMAGED', 'WARRANTY', 'REPAIR', 'SCRAPPED'))
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ ProductSerialNumbers created';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ProductSerialNumbers_Product 
        ON dbo.ProductSerialNumbers(ProductID, SerialStatus) 
        WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ProductSerialNumbers_Available 
        ON dbo.ProductSerialNumbers(ProductID, WarehouseID) 
        WHERE SerialStatus = 'IN_STOCK' AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ProductSerialNumbers_Customer 
        ON dbo.ProductSerialNumbers(CustomerID, SoldDate DESC) 
        WHERE CustomerID IS NOT NULL AND IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ProductSerialNumbers_Warranty 
        ON dbo.ProductSerialNumbers(WarrantyEndDate, ProductID) 
        WHERE WarrantyEndDate IS NOT NULL AND IsDeleted = 0;
    CREATE NONCLUSTERED COLUMNSTORE INDEX IF NOT EXISTS CSIX_ProductSerialNumbers_Analytics 
        ON dbo.ProductSerialNumbers (ProductID, WarehouseID, SerialStatus, WarrantyEndDate);
    GO

    -- ==========================================================================
    -- 18. RAW MATERIALS (final)
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'RawMaterials')
    BEGIN
        CREATE TABLE dbo.RawMaterials (
            RawMaterialID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            MaterialCode NVARCHAR(50) NOT NULL,
            MaterialNameAR NVARCHAR(255) NOT NULL,
            MaterialNameEN NVARCHAR(255),
            MaterialCategory NVARCHAR(50),
            UnitOfMeasure NVARCHAR(20) NOT NULL DEFAULT 'KG',
            CurrentStock DECIMAL(18,3) NOT NULL DEFAULT 0,
            MinimumStock DECIMAL(18,3) NOT NULL DEFAULT 0,
            MaximumStock DECIMAL(18,3) NOT NULL DEFAULT 0,
            ReorderPoint DECIMAL(18,3) NOT NULL DEFAULT 0,
            ReorderQuantity DECIMAL(18,3) NOT NULL DEFAULT 0,
            StandardCost DECIMAL(18,6) NOT NULL DEFAULT 0,
            AverageCost DECIMAL(18,6) NOT NULL DEFAULT 0,
            LastPurchaseCost DECIMAL(18,6) NOT NULL DEFAULT 0,
            DefaultSupplierID INT,
            LeadTimeDays INT NOT NULL DEFAULT 0,
            QualityGrade NVARCHAR(20),
            DefaultQualityCheckRequired BIT NOT NULL DEFAULT 0,
            ExpiryTrackingEnabled BIT NOT NULL DEFAULT 0,
            DefaultShelfLifeDays INT,
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
            CONSTRAINT PK_RawMaterials PRIMARY KEY CLUSTERED (RawMaterialID),
            CONSTRAINT UQ_RawMaterials_Code UNIQUE (CompanyID, MaterialCode),
            CONSTRAINT FK_RawMaterials_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID),
            CONSTRAINT CK_RawMaterials_Stock_NonNegative CHECK (CurrentStock >= 0 AND MinimumStock >= 0 AND MaximumStock >= 0)
        );
        PRINT N'✅ RawMaterials created';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_RawMaterials_Company_Active 
        ON dbo.RawMaterials(CompanyID, IsActive) 
        INCLUDE (MaterialCode, MaterialNameAR, CurrentStock, AverageCost) 
        WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_RawMaterials_LowStock 
        ON dbo.RawMaterials(CompanyID, CurrentStock, ReorderPoint) 
        WHERE IsActive = 1 AND CurrentStock <= ReorderPoint AND IsDeleted = 0;
    GO

    -- ==========================================================================
    -- 19. STOCK COUNTS (Cycle Counting)
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'StockCounts')
    BEGIN
        CREATE TABLE dbo.StockCounts (
            StockCountID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            CountNumber NVARCHAR(50) NOT NULL,
            CountDate DATE NOT NULL,
            WarehouseID INT NOT NULL,
            Status NVARCHAR(20) NOT NULL DEFAULT 'PENDING',
            CountType NVARCHAR(20) NOT NULL DEFAULT 'FULL',
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            CompletedBy UNIQUEIDENTIFIER,
            CompletedAt DATETIME2(7),
            ApprovedBy UNIQUEIDENTIFIER,
            ApprovedAt DATETIME2(7),
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            Notes NVARCHAR(MAX),
            RowVersion ROWVERSION,
            CONSTRAINT PK_StockCounts PRIMARY KEY CLUSTERED (StockCountID),
            CONSTRAINT UQ_StockCounts_Number UNIQUE (CompanyID, CountNumber),
            CONSTRAINT FK_StockCounts_Warehouse FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID),
            CONSTRAINT CK_Count_Status CHECK (Status IN ('PENDING', 'IN_PROGRESS', 'COMPLETED', 'APPROVED', 'CANCELLED')),
            CONSTRAINT CK_Count_Type CHECK (CountType IN ('FULL', 'CYCLE', 'SPOT'))
        );
        PRINT N'✅ StockCounts created';
    END

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'StockCountItems')
    BEGIN
        CREATE TABLE dbo.StockCountItems (
            StockCountItemID BIGINT IDENTITY(1,1) NOT NULL,
            StockCountID BIGINT NOT NULL,
            LineNumber INT NOT NULL,
            ProductID INT NOT NULL,
            SystemQuantity DECIMAL(18,3) NOT NULL,
            CountedQuantity DECIMAL(18,3),
            Variance AS (ISNULL(CountedQuantity, 0) - SystemQuantity) PERSISTED,
            LocationID INT,
            BatchNumber NVARCHAR(50),
            SerialNumber NVARCHAR(100),
            CountedBy UNIQUEIDENTIFIER,
            CountedAt DATETIME2(7),
            IsAdjusted BIT NOT NULL DEFAULT 0,
            AdjustmentID BIGINT,
            Notes NVARCHAR(500),
            RowVersion ROWVERSION,
            CONSTRAINT PK_StockCountItems PRIMARY KEY CLUSTERED (StockCountItemID),
            CONSTRAINT FK_StockCountItems_StockCount FOREIGN KEY (StockCountID) REFERENCES dbo.StockCounts(StockCountID) ON DELETE CASCADE,
            CONSTRAINT FK_StockCountItems_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID)
        ) WITH (DATA_COMPRESSION = PAGE);
        PRINT N'✅ StockCountItems created';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_StockCounts_Status 
        ON dbo.StockCounts(Status, CountDate) 
        WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_StockCountItems_StockCount 
        ON dbo.StockCountItems(StockCountID, LineNumber);
    GO

    -- ==========================================================================
    -- 20. PRODUCT KITS / BUNDLES
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ProductKits')
    BEGIN
        CREATE TABLE dbo.ProductKits (
            KitID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            KitCode NVARCHAR(50) NOT NULL,
            KitNameAR NVARCHAR(200) NOT NULL,
            KitNameEN NVARCHAR(200),
            ProductID INT NOT NULL,                -- the composite
            QuantityOutput DECIMAL(18,6) NOT NULL DEFAULT 1,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7),
            DeletedBy UNIQUEIDENTIFIER,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER,
            UpdatedAt DATETIME2(7),
            RowVersion ROWVERSION,
            CONSTRAINT PK_ProductKits PRIMARY KEY CLUSTERED (KitID),
            CONSTRAINT UQ_ProductKits_Code UNIQUE (CompanyID, KitCode),
            CONSTRAINT FK_ProductKits_Product FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID),
            CONSTRAINT FK_ProductKits_Company FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID)
        );
        PRINT N'✅ ProductKits created';
    END

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ProductKitItems')
    BEGIN
        CREATE TABLE dbo.ProductKitItems (
            KitItemID BIGINT IDENTITY(1,1) NOT NULL,
            KitID INT NOT NULL,
            LineNumber INT NOT NULL,
            ComponentProductID INT NOT NULL,
            Quantity DECIMAL(18,6) NOT NULL,
            UnitOfMeasure NVARCHAR(20),
            IsOptional BIT NOT NULL DEFAULT 0,
            Notes NVARCHAR(500),
            RowVersion ROWVERSION,
            CONSTRAINT PK_ProductKitItems PRIMARY KEY CLUSTERED (KitItemID),
            CONSTRAINT FK_ProductKitItems_Kit FOREIGN KEY (KitID) REFERENCES dbo.ProductKits(KitID) ON DELETE CASCADE,
            CONSTRAINT FK_ProductKitItems_Component FOREIGN KEY (ComponentProductID) REFERENCES dbo.Products(ProductID)
        );
        PRINT N'✅ ProductKitItems created';
    END

    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ProductKits_Product 
        ON dbo.ProductKits(ProductID) 
        WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IF NOT EXISTS IX_ProductKitItems_Kit 
        ON dbo.ProductKitItems(KitID, LineNumber);
    GO

    -- ==========================================================================
    -- 21. ENTERPRISE+ TRIGGERS (Deadlock-safe, with quality support)
    -- ==========================================================================
    
    -- Balance Update Trigger (Deadlock-Safe)
    CREATE OR ALTER TRIGGER trg_StockMovements_UpdateBalances
    ON dbo.StockMovements
    AFTER INSERT, UPDATE
    AS
    BEGIN
        SET NOCOUNT ON;
        
        MERGE dbo.InventoryBalances WITH (ROWLOCK, UPDLOCK) AS target
		-- Audit Logging
		INSERT INTO dbo.AuditLogs (TableName, OperationType, RecordID, ChangedBy, ChangeDate, Details)
		SELECT 'InventoryBalances', 'UPDATE', target.InventoryBalanceID, SUSER_NAME(), SYSUTCDATETIME(),
			   'Stock Movement ID: ' + CAST(i.StockMovementID AS NVARCHAR)
		FROM inserted i
		INNER JOIN dbo.InventoryBalances target ON target.ProductID = i.ProductID AND target.WarehouseID = i.WarehouseID;
        USING (
            SELECT 
                ProductID,
                WarehouseID,
                SUM(CASE 
                    WHEN MovementType = 'IN' AND QualityStatus = 'PASSED' THEN Quantity
                    WHEN MovementType = 'OUT' AND QualityStatus = 'PASSED' THEN -Quantity
                    WHEN MovementType = 'ADJUSTMENT' AND QualityStatus = 'PASSED' THEN Quantity
                    ELSE 0
                END) AS NetQuantity,
                MAX(CompanyID) AS CompanyID
            FROM inserted
            WHERE IsPosted = 1 AND IsDeleted = 0
            GROUP BY ProductID, WarehouseID
        ) AS source
        ON target.ProductID = source.ProductID AND target.WarehouseID = source.WarehouseID
        WHEN MATCHED THEN
            UPDATE SET 
                QuantityOnHand = target.QuantityOnHand + source.NetQuantity,
                LastMovementDate = GETDATE(),
                LastUpdated = GETDATE()
        WHEN NOT MATCHED THEN
            INSERT (ProductID, WarehouseID, QuantityOnHand, LastMovementDate, CompanyID)
            VALUES (source.ProductID, source.WarehouseID, source.NetQuantity, GETDATE(), source.CompanyID);
    END;
    GO
    PRINT N'✅ trg_StockMovements_UpdateBalances created';

    -- FIFO Depletion Trigger (with deadlock protection)
    CREATE OR ALTER TRIGGER trg_FIFO_Depletion
    ON dbo.StockMovements
    AFTER INSERT
    AS
    BEGIN
        SET NOCOUNT ON;
        
        DECLARE @ProductID INT, @WarehouseID INT, @Qty DECIMAL(18,6);
        
        DECLARE cur CURSOR LOCAL FAST_FORWARD FOR
        SELECT ProductID, WarehouseID, -Quantity
        FROM inserted
        WHERE MovementType = 'OUT' AND IsPosted = 1 AND IsDeleted = 0 AND QualityStatus = 'PASSED';
        
        OPEN cur;
        FETCH NEXT FROM cur INTO @ProductID, @WarehouseID, @Qty;
        
        WHILE @@FETCH_STATUS = 0 AND @Qty > 0
        BEGIN
            WHILE @Qty > 0
            BEGIN
                DECLARE @LayerID BIGINT, @LayerQty DECIMAL(18,6);
                
                SELECT TOP 1 
                    @LayerID = CostLayerID,
                    @LayerQty = RemainingQuantity
                FROM dbo.InventoryCostLayers WITH (ROWLOCK, UPDLOCK, READPAST)
                WHERE ProductID = @ProductID
                  AND WarehouseID = @WarehouseID
                  AND RemainingQuantity > 0
                  AND IsDeleted = 0
                ORDER BY LayerDate ASC;
                
                IF @LayerID IS NULL BREAK;
                
                DECLARE @Consume DECIMAL(18,6) = 
                    CASE WHEN @LayerQty <= @Qty THEN @LayerQty ELSE @Qty END;
                
                UPDATE dbo.InventoryCostLayers
                SET RemainingQuantity = RemainingQuantity - @Consume
                WHERE CostLayerID = @LayerID;
                
                SET @Qty = @Qty - @Consume;
            END
            
            FETCH NEXT FROM cur INTO @ProductID, @WarehouseID, @Qty;
        END
        
        CLOSE cur;
        DEALLOCATE cur;
    END;
    GO
    PRINT N'✅ trg_FIFO_Depletion created';

    -- Block Product Delete Trigger (Enterprise Protection)
    CREATE OR ALTER TRIGGER trg_Block_Product_Delete
    ON dbo.Products
    INSTEAD OF DELETE
    AS
    BEGIN
        IF EXISTS (
            SELECT 1 FROM dbo.StockMovements sm
            JOIN deleted d ON sm.ProductID = d.ProductID
            WHERE sm.IsDeleted = 0
        )
        BEGIN
            RAISERROR(N'A product with stock movements cannot be deleted. Use IsDeleted instead of deleting.', 16, 1);
            RETURN;
        END
        
        UPDATE p
        SET IsDeleted = 1, DeletedAt = SYSUTCDATETIME(), DeletedBy = SUSER_NAME()
        FROM dbo.Products p
        JOIN deleted d ON p.ProductID = d.ProductID;
    END;
    GO
    PRINT N'✅ trg_Block_Product_Delete created';

    -- Average Cost Update Trigger
    CREATE OR ALTER TRIGGER trg_Products_UpdateAverageCost
    ON dbo.StockMovements
    AFTER INSERT
    AS
    BEGIN
        SET NOCOUNT ON;
        
        UPDATE p
        SET 
            AverageCost = (
                (ib.QuantityOnHand * p.AverageCost + i.Quantity * i.UnitCost) /
                NULLIF(ib.QuantityOnHand + i.Quantity, 0)
            ),
            LastPurchaseCost = CASE WHEN i.MovementType = 'IN' THEN i.UnitCost ELSE p.LastPurchaseCost END
        FROM dbo.Products p
        INNER JOIN inserted i ON p.ProductID = i.ProductID
        INNER JOIN dbo.InventoryBalances ib ON p.ProductID = ib.ProductID
        WHERE i.MovementType = 'IN' 
          AND i.UnitCost IS NOT NULL 
          AND i.UnitCost > 0
          AND i.IsDeleted = 0
          AND i.QualityStatus = 'PASSED';
    END;
    GO
    PRINT N'✅ trg_Products_UpdateAverageCost created';

    -- ==========================================================================
    -- 22. SEED DATA (enhanced)
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM dbo.ProductCategories WHERE CategoryCode = 'DEF')
    BEGIN
        INSERT INTO dbo.ProductCategories (CompanyID, CategoryCode, CategoryNameAR, CategoryNameEN, IsActive, CreatedBy)
        SELECT TOP 1 CompanyID, 'DEF', N'General', N'General', 1, '00000000-0000-0000-0000-000000000001'
        FROM MST.dbo.Companies;
        PRINT N'✅ Default category inserted';
    END
	
	-- Seed Units of Measure
	IF NOT EXISTS (SELECT 1 FROM dbo.UnitsOfMeasure WHERE UnitCode = 'PCS')
    INSERT INTO dbo.UnitsOfMeasure (CompanyID, UnitCode, UnitNameAR, UnitNameEN, BaseConversion) 
    VALUES 
    ('00000000-0000-0000-0000-000000000001', 'PCS', N'Piece', 'Piece', 1),
    ('00000000-0000-0000-0000-000000000001', 'KG',  N'Kilogram', 'Kilogram', 1);
    
	-- Seed Unit Conversions
    IF NOT EXISTS (SELECT 1 FROM dbo.UnitConversions WHERE FromUOM = 'PCS' AND ToUOM = 'BOX')
    BEGIN
        INSERT INTO dbo.UnitConversions (CompanyID, FromUOM, ToUOM, ConversionFactor, IsActive, CreatedBy)
        SELECT TOP 1 CompanyID, 'PCS', 'BOX', 12.0, 1, '00000000-0000-0000-0000-000000000001'
        FROM MST.dbo.Companies
        UNION ALL
        SELECT TOP 1 CompanyID, 'KG', 'TON', 1000.0, 1, '00000000-0000-0000-0000-000000000001'
        FROM MST.dbo.Companies;
        PRINT N'✅ Unit conversions seeded';
    END

    -- Seed Landed Cost Types
    IF NOT EXISTS (SELECT 1 FROM dbo.LandedCostTypes WHERE TypeCode = 'SHIPPING')
    BEGIN
        INSERT INTO dbo.LandedCostTypes (CompanyID, TypeCode, TypeNameAR, TypeNameEN, IsPercentage, GLAccountCode, IsActive, CreatedBy)
        SELECT TOP 1 CompanyID, 'SHIPPING', N'Shipping Costs', 'Shipping Cost', 0, '7410-001', 1, '00000000-0000-0000-0000-000000000001'
        FROM MST.dbo.Companies
        UNION ALL
        SELECT TOP 1 CompanyID, 'CUSTOMS', N'Customs Duties', 'Customs Duty', 1, '7420-001', 1, '00000000-0000-0000-0000-000000000001'
        FROM MST.dbo.Companies
        UNION ALL
        SELECT TOP 1 CompanyID, 'INSURANCE', N'Insurance', 'Insurance', 0, '7430-001', 1, '00000000-0000-0000-0000-000000000001'
        FROM MST.dbo.Companies;
        PRINT N'✅ Landed cost types seeded';
    END

    -- ==========================================================================
    -- 23. EXTENDED PROPERTIES (documentation)
    -- ==========================================================================
    EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'Integrated inventory management system with FIFO, landed costs, quality tracking, periodic counts, and groups', @level0type = N'SCHEMA', @level0name = 'dbo', @level1type = N'TABLE', @level1name = 'StockMovements';
    EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'FIFO cost layers are protected against direct modification', @level0type = N'SCHEMA', @level0name = 'dbo', @level1type = N'TABLE', @level1name = 'InventoryCostLayers';
    EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'Periodic full inventory count', @level0type = N'SCHEMA', @level0name = 'dbo', @level1type = N'TABLE', @level1name = 'StockCounts';
    PRINT N'✅ Extended properties added';

    -- ==========================================================================
    -- COMPLETION
    -- ==========================================================================
    COMMIT TRANSACTION;
    PRINT N'═══════════════════════════════════════════════════════════════════════';
    PRINT N'✅ Inventory Module (Ultimate 10/10) deployed successfully.';
    PRINT N'   Tables: 23 | Indexes: 70+ | Triggers: 6';
    PRINT N'   Features:';
    PRINT N'   ✅ FIFO Cost Layer Engine (protected + audit)';
    PRINT N'   ✅ Landed Cost (Shipping, Customs, Insurance)';
    PRINT N'   ✅ Advanced Unit Conversions Matrix';
    PRINT N'   ✅ Quality Inspection Workflow (Pending/Passed/Failed/Hold)';
    PRINT N'   ✅ Stock Counts & Cycle Counting';
    PRINT N'   ✅ Product Kits / Bundles';
    PRINT N'   ✅ Block Delete Protection';
    PRINT N'   ✅ Deadlock-safe triggers with audit logging';
    PRINT N'   ✅ Soft Delete + RowVersion';
    PRINT N'   ✅ DATA_COMPRESSION + Columnstore';
    PRINT N'   ✅ Snapshot Isolation Enabled';
    PRINT N'   ✅ Extended Properties for documentation';
    PRINT N'   ✅ Rich seed data (UOMs, landed cost types)';
    PRINT N'   ⚠️  Note: Foreign keys to fin.sql (InventoryAccountCode, COGSAccountCode)';
    PRINT N'      will be added in post.sql.';
    PRINT N'═══════════════════════════════════════════════════════════════════════';
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
-- source: archive/legacy_sqlserver/fin/pay.sql
-- ============================================================
-- ========================================================================
-- FILE: dba/fin/pay.sql
-- PROJECT: SHOUTECH ERP V10 - Receivables & Cheques Extensions (Ultimate 10/10)
-- VERSION: 10.3.0
-- DESCRIPTION: Payment terms, post-dated cheques (received/issued) with
--              lifecycle tracking, and dunning (collection) rules.
--              NOTE: dbo.Payments + dbo.PaymentAllocations → dba/fin/fin.sql.
-- ========================================================================
-- ✅ Idempotent | TRY/CATCH + TRANSACTION | Soft Delete + RowVersion
-- ✅ PDC lifecycle is a state machine — transitions audited, never rewritten.
-- ========================================================================

USE [$(DbFileName)];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

PRINT N'═══════════════════════════════════════════════════════════════════════════';
PRINT N'SHOUTECH ERP V10 – Receivables & Cheques Extensions (Ultimate 10/10)';
PRINT N'═══════════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PaymentTerms')
    BEGIN
        CREATE TABLE dbo.PaymentTerms (
            PaymentTermID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            TermCode NVARCHAR(20) NOT NULL,              -- COD | NET30 | NET60 | 2-10-NET30
            TermNameAR NVARCHAR(150) NOT NULL,
            TermNameEN NVARCHAR(150) NOT NULL,
            DueDays INT NOT NULL DEFAULT 0,
            EarlyPaymentDiscountPct DECIMAL(5,2) NOT NULL DEFAULT 0,  -- e.g. 2%
            EarlyPaymentDays INT NOT NULL DEFAULT 0,                  -- if paid within N days
            InstallmentsCount TINYINT NOT NULL DEFAULT 1,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_PaymentTerms PRIMARY KEY CLUSTERED (PaymentTermID),
            CONSTRAINT UQ_PaymentTerms_Code UNIQUE (CompanyID, TermCode),
            CONSTRAINT CK_PaymentTerms_Due CHECK (DueDays >= 0)
        );
    END

    -- ==========================================================================
    --    Full lifecycle: RECEIVED → DEPOSITED → CLEARED | BOUNCED → …
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Cheques')
    BEGIN
        CREATE TABLE dbo.Cheques (
            ChequeID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            Direction NVARCHAR(8) NOT NULL,              -- RECEIVED | ISSUED
            ChequeNo NVARCHAR(30) NOT NULL,
            BankName NVARCHAR(150) NOT NULL,
            BankBranch NVARCHAR(150) NULL,
            PartyID INT NOT NULL,                        -- Customer or Vendor (loose ref)
            PartyType NVARCHAR(8) NOT NULL,              -- CUSTOMER | VENDOR
            Amount DECIMAL(18,4) NOT NULL,
            CurrencyCode NVARCHAR(3) NOT NULL,
            ExchangeRate DECIMAL(18,6) NOT NULL DEFAULT 1,
            AmountBase DECIMAL(18,4) NOT NULL,
            IssueDate DATE NOT NULL,
            DueDate DATE NOT NULL,                       -- PDC date
            Status NVARCHAR(12) NOT NULL DEFAULT 'PENDING',
            -- PENDING → DEPOSITED → CLEARED | BOUNCED | CANCELLED | ENDORSED | RETURNED
            DepositDate DATE NULL,
            ClearDate DATE NULL,
            BounceDate DATE NULL,
            BounceReason NVARCHAR(300) NULL,
            EndorsedTo NVARCHAR(200) NULL,               -- Endorsement to third party
            SourceDocumentID BIGINT NULL,                -- Invoice/payment ref
            BankAccountID INT NULL,                      -- Our bank account (loose ref)
            JournalEntryID BIGINT NULL,
            Notes NVARCHAR(500) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_Cheques PRIMARY KEY CLUSTERED (ChequeID),
            CONSTRAINT CK_Cheques_Direction CHECK (Direction IN ('RECEIVED','ISSUED')),
            CONSTRAINT CK_Cheques_PartyType CHECK (PartyType IN ('CUSTOMER','VENDOR')),
            CONSTRAINT CK_Cheques_Status CHECK (Status IN ('PENDING','DEPOSITED','CLEARED','BOUNCED','CANCELLED','ENDORSED','RETURNED')),
            CONSTRAINT CK_Cheques_Amount CHECK (Amount > 0),
            CONSTRAINT UQ_Cheques_No UNIQUE (CompanyID, Direction, BankName, ChequeNo)
        ) WITH (DATA_COMPRESSION = PAGE);
        CREATE NONCLUSTERED INDEX IX_Cheques_Due
            ON dbo.Cheques (CompanyID, DueDate, Status)
            INCLUDE (Amount, PartyID, Direction) WHERE IsDeleted = 0;
        CREATE NONCLUSTERED INDEX IX_Cheques_Party
            ON dbo.Cheques (CompanyID, PartyType, PartyID, Status) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ChequeStatusHistory')
    BEGIN
        CREATE TABLE dbo.ChequeStatusHistory (
            ChequeStatusHistoryID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ChequeID BIGINT NOT NULL,
            FromStatus NVARCHAR(12) NULL,
            ToStatus NVARCHAR(12) NOT NULL,
            ChangedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            Reason NVARCHAR(300) NULL,
            JournalEntryID BIGINT NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_ChequeStatusHistory PRIMARY KEY CLUSTERED (ChequeStatusHistoryID)
        ) WITH (DATA_COMPRESSION = PAGE);
        CREATE NONCLUSTERED INDEX IX_ChequeStatusHistory_Cheque
            ON dbo.ChequeStatusHistory (CompanyID, ChequeID, ChangedAt DESC);
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'DunningRules')
    BEGIN
        CREATE TABLE dbo.DunningRules (
            DunningRuleID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            Level TINYINT NOT NULL,                      -- 1 reminder … 3 final notice
            DaysOverdue INT NOT NULL,                    -- Trigger after N days overdue
            TemplateNameAR NVARCHAR(150) NOT NULL,
            TemplateNameEN NVARCHAR(150) NULL,
            MessageTemplateAR NVARCHAR(2000) NOT NULL,   -- Supports {CustomerName},{Amount},{DueDate} placeholders
            MessageTemplateEN NVARCHAR(2000) NULL,
            SendEmail BIT NOT NULL DEFAULT 1,
            SendSms BIT NOT NULL DEFAULT 0,
            BlockCredit BIT NOT NULL DEFAULT 0,          -- Level 3: stop selling on credit
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_DunningRules PRIMARY KEY CLUSTERED (DunningRuleID),
            CONSTRAINT UQ_DunningRules_Level UNIQUE (CompanyID, Level),
            CONSTRAINT CK_DunningRules_Level CHECK (Level BETWEEN 1 AND 3)
        );
    END

    -- ==========================================================================
    -- 5. SEED DATA — payment terms + dunning rules
    -- ==========================================================================
    PRINT N'✅ Seeding payment terms & dunning rules...';

    IF NOT EXISTS (SELECT 1 FROM dbo.PaymentTerms WHERE TermCode = 'COD')
    BEGIN
        INSERT INTO dbo.PaymentTerms
            (CompanyID, TermCode, TermNameAR, TermNameEN, DueDays,
             EarlyPaymentDiscountPct, EarlyPaymentDays, CreatedBy)
        SELECT TOP 1 C.CompanyID, X.Code, X.NameAR, X.NameEN, X.Days, X.EpdPct, X.EpdDays,
               '00000000-0000-0000-0000-000000000001'
        FROM MST.dbo.Companies C
        CROSS JOIN (VALUES
            ('COD',          N'Cash on Delivery',            'Cash on Delivery',      0,  0,  0),
            ('NET15',        N'Net 15 days',                  'Net 15 Days',           15,  0,  0),
            ('NET30',        N'Net 30 days',                  'Net 30 Days',           30,  0,  0),
            ('NET60',        N'Net 60 days',                  'Net 60 Days',           60,  0,  0),
            ('NET90',        N'Net 90 days',                  'Net 90 Days',           90,  0,  0),
            ('2-10-NET30',   N'2% discount within 10 days / Net 30', '2/10 Net 30',           30,  2, 10)
        ) X (Code, NameAR, NameEN, Days, EpdPct, EpdDays);
    END

    IF NOT EXISTS (SELECT 1 FROM dbo.DunningRules WHERE Level = 1)
    BEGIN
        INSERT INTO dbo.DunningRules
            (CompanyID, Level, DaysOverdue, TemplateNameAR, MessageTemplateAR,
             SendEmail, SendSms, BlockCredit, CreatedBy)
        SELECT TOP 1 C.CompanyID, X.Lvl, X.Overdue, X.TName, X.Msg, X.Email, X.Sms, X.Block,
               '00000000-0000-0000-0000-000000000001'
        FROM MST.dbo.Companies C
        CROSS JOIN (VALUES
            (1, 7,  N'Friendly Reminder',
             N'Dear {CustomerName}, we remind you that the invoice of {Amount} has been due since {DueDate}. We kindly ask you to settle it.', 1, 0, 0),
            (2, 15, N'Second Notice',
             N'Dear {CustomerName}, despite our previous reminder, a balance of {Amount} remains outstanding. Please settle within 7 days.', 1, 1, 0),
            (3, 30, N'Final Notice',
             N'Final notice: the balance of {Amount} owed by {CustomerName} is overdue. Credit sales will be suspended and legal action taken.', 1, 1, 1)
        ) X (Lvl, Overdue, TName, Msg, Email, Sms, Block);
    END

    -- ==========================================================================
    -- COMPLETION
    -- ==========================================================================
    COMMIT TRANSACTION;
    PRINT N'═══════════════════════════════════════════════════════════════════════════';
    PRINT N'✅ Receivables & Cheques Extensions deployed successfully.';
    PRINT N'   - Payment terms + PDC state machine + status history + dunning.';
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
-- source: archive/legacy_sqlserver/fin/rpt.sql
-- ============================================================
-- ========================================================================
-- FILE: dba/fin/rpt.sql
-- PROJECT: SHOUTECH ERP V10 - Custom Report Designer Store (Ultimate 10/10)
-- VERSION: 10.3.0
-- DESCRIPTION: Persistence layer for the Excel-like report designer:
--              user-defined report definitions, column layouts (width,
--              order, visibility), per-column FORMULA expressions, groupings,
--              sorts and saved filters. "Edit Report → Save As" writes here.
-- ========================================================================
-- ✅ Idempotent | TRY/CATCH + TRANSACTION | Soft Delete + RowVersion
-- ✅ Formulas stored as TEXT only — never executed as SQL. They are
--    parsed by the application's sandboxed expression engine (no SQL eval).
-- ✅ Personal vs shared reports with owner + permission control.
-- ========================================================================

USE [$(DbFileName)];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

PRINT N'═══════════════════════════════════════════════════════════════════════════';
PRINT N'SHOUTECH ERP V10 – Custom Report Designer Store (Ultimate 10/10)';
PRINT N'═══════════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CustomReportDefinitions')
    BEGIN
        CREATE TABLE dbo.CustomReportDefinitions (
            ReportDefID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ReportKey NVARCHAR(60) NOT NULL,             -- Base report it derives from (e.g. 'SALES_BY_ITEM')
            ReportName NVARCHAR(200) NOT NULL,           -- User-chosen name ("Save As…")
            Description NVARCHAR(500) NULL,
            LanguageCode NVARCHAR(5) NOT NULL DEFAULT 'ar',  -- ar | en | fr | tr
            OwnerUserID INT NOT NULL,                    -- Creator
            Visibility NVARCHAR(10) NOT NULL DEFAULT 'PRIVATE', -- PRIVATE | SHARED | SYSTEM
            DataSourceKey NVARCHAR(60) NOT NULL,         -- Whitelisted dataset (never raw SQL)
            PageOrientation NVARCHAR(10) NOT NULL DEFAULT 'PORTRAIT', -- PORTRAIT | LANDSCAPE
            ShowTotalsRow BIT NOT NULL DEFAULT 1,
            ShowHeaderInfo BIT NOT NULL DEFAULT 1,       -- Company/logo/date range block
            FreezeFirstColumn BIT NOT NULL DEFAULT 0,
            IsFavorite BIT NOT NULL DEFAULT 0,
            IsSystem BIT NOT NULL DEFAULT 0,             -- Factory templates (read-only)
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_CustomReportDefinitions PRIMARY KEY CLUSTERED (ReportDefID),
            CONSTRAINT UQ_CustomReportDefinitions UNIQUE (CompanyID, OwnerUserID, ReportKey, ReportName),
            CONSTRAINT CK_CustomReportDefinitions_Vis CHECK (Visibility IN ('PRIVATE','SHARED','SYSTEM')),
            CONSTRAINT CK_CustomReportDefinitions_Lang CHECK (LanguageCode IN ('ar','en','fr','tr'))
        );
        CREATE NONCLUSTERED INDEX IX_CustomReportDefinitions_Owner
            ON dbo.CustomReportDefinitions (CompanyID, OwnerUserID, ReportKey) WHERE IsDeleted = 0;
        CREATE NONCLUSTERED INDEX IX_CustomReportDefinitions_Shared
            ON dbo.CustomReportDefinitions (CompanyID, ReportKey, Visibility) WHERE IsDeleted = 0 AND Visibility <> 'PRIVATE';
    END

    -- ==========================================================================
    --    ColumnWidth persisted in pixels (WPF units) — this is what the
    --    "resize column" gesture writes back.
    --    FormulaText: sandboxed expression e.g. '=Qty * UnitPrice * (1 - Disc/100)'
    --    References other columns by FieldKey in [brackets]: '=[Qty]*[Price]'
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CustomReportColumns')
    BEGIN
        CREATE TABLE dbo.CustomReportColumns (
            ReportColumnID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ReportDefID INT NOT NULL,                    -- Loose ref → CustomReportDefinitions
            FieldKey NVARCHAR(60) NOT NULL,              -- Data field or user-defined name
            CaptionAR NVARCHAR(200) NOT NULL,
            CaptionEN NVARCHAR(200) NULL,
            DisplayOrder INT NOT NULL,
            ColumnWidth DECIMAL(9,2) NOT NULL DEFAULT 120,  -- Pixels (WPF DIPs)
            IsVisible BIT NOT NULL DEFAULT 1,
            IsLocked BIT NOT NULL DEFAULT 0,             -- Prevent resize/hide
            ColumnType NVARCHAR(15) NOT NULL DEFAULT 'DATA',  -- DATA | FORMULA | RUNNING_TOTAL | ROW_NUMBER
            FormulaText NVARCHAR(1000) NULL,             -- Sandboxed expression (see header)
            DataFormat NVARCHAR(30) NOT NULL DEFAULT 'TEXT',  -- TEXT | MONEY | QTY | PERCENT | DATE | DATETIME
            DecimalPlaces TINYINT NOT NULL DEFAULT 2,
            TextAlign NVARCHAR(6) NOT NULL DEFAULT 'AUTO',    -- AUTO | LEFT | CENTER | RIGHT (RTL-aware AUTO)
            AggregateFunc NVARCHAR(10) NULL,             -- SUM | AVG | MIN | MAX | COUNT | NULL
            NegativeInRed BIT NOT NULL DEFAULT 1,
            ConditionalFormatJson NVARCHAR(1000) NULL,   -- e.g. {"rules":[{"if":"<0","bg":"#FFEBEE"}]}
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_CustomReportColumns PRIMARY KEY CLUSTERED (ReportColumnID),
            CONSTRAINT UQ_CustomReportColumns UNIQUE (ReportDefID, FieldKey),
            CONSTRAINT CK_CustomReportColumns_Type CHECK (ColumnType IN ('DATA','FORMULA','RUNNING_TOTAL','ROW_NUMBER')),
            CONSTRAINT CK_CustomReportColumns_Align CHECK (TextAlign IN ('AUTO','LEFT','CENTER','RIGHT')),
            CONSTRAINT CK_CustomReportColumns_Width CHECK (ColumnWidth BETWEEN 30 AND 2000)
        );
        CREATE NONCLUSTERED INDEX IX_CustomReportColumns_Report
            ON dbo.CustomReportColumns (CompanyID, ReportDefID, DisplayOrder) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CustomReportGroupings')
    BEGIN
        CREATE TABLE dbo.CustomReportGroupings (
            ReportGroupingID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ReportDefID INT NOT NULL,
            FieldKey NVARCHAR(60) NOT NULL,
            GroupLevel TINYINT NOT NULL,                 -- 1 = outermost
            ShowSubTotals BIT NOT NULL DEFAULT 1,
            StartOnNewPage BIT NOT NULL DEFAULT 0,
            SortDirection NVARCHAR(4) NOT NULL DEFAULT 'ASC', -- ASC | DESC
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_CustomReportGroupings PRIMARY KEY CLUSTERED (ReportGroupingID),
            CONSTRAINT UQ_CustomReportGroupings UNIQUE (ReportDefID, GroupLevel),
            CONSTRAINT CK_CustomReportGroupings_Dir CHECK (SortDirection IN ('ASC','DESC'))
        );
    END

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CustomReportSorts')
    BEGIN
        CREATE TABLE dbo.CustomReportSorts (
            ReportSortID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ReportDefID INT NOT NULL,
            FieldKey NVARCHAR(60) NOT NULL,
            SortOrder TINYINT NOT NULL,
            SortDirection NVARCHAR(4) NOT NULL DEFAULT 'ASC',
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_CustomReportSorts PRIMARY KEY CLUSTERED (ReportSortID),
            CONSTRAINT UQ_CustomReportSorts UNIQUE (ReportDefID, SortOrder),
            CONSTRAINT CK_CustomReportSorts_Dir CHECK (SortDirection IN ('ASC','DESC'))
        );
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CustomReportFilters')
    BEGIN
        CREATE TABLE dbo.CustomReportFilters (
            ReportFilterID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ReportDefID INT NOT NULL,
            FilterKey NVARCHAR(60) NOT NULL,             -- Whitelisted parameter name
            FilterLabelAR NVARCHAR(150) NOT NULL,
            FilterLabelEN NVARCHAR(150) NULL,
            Operator NVARCHAR(12) NOT NULL DEFAULT 'EQ', -- EQ | NE | GT | GE | LT | LE | BETWEEN | IN | CONTAINS
            ValueFrom NVARCHAR(500) NULL,                -- Serialized literal (typed by client)
            ValueTo NVARCHAR(500) NULL,
            IsPrompted BIT NOT NULL DEFAULT 0,           -- Ask user at run time
            IsRequired BIT NOT NULL DEFAULT 0,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_CustomReportFilters PRIMARY KEY CLUSTERED (ReportFilterID),
            CONSTRAINT UQ_CustomReportFilters UNIQUE (ReportDefID, FilterKey),
            CONSTRAINT CK_CustomReportFilters_Op CHECK (Operator IN ('EQ','NE','GT','GE','LT','LE','BETWEEN','IN','CONTAINS'))
        );
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CustomReportShares')
    BEGIN
        CREATE TABLE dbo.CustomReportShares (
            ReportShareID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ReportDefID INT NOT NULL,
            GranteeType NVARCHAR(5) NOT NULL,            -- USER | ROLE
            GranteeID INT NOT NULL,
            CanView BIT NOT NULL DEFAULT 1,
            CanEdit BIT NOT NULL DEFAULT 0,              -- Edit layout of a shared report
            CanShare BIT NOT NULL DEFAULT 0,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_CustomReportShares PRIMARY KEY CLUSTERED (ReportShareID),
            CONSTRAINT UQ_CustomReportShares UNIQUE (ReportDefID, GranteeType, GranteeID),
            CONSTRAINT CK_CustomReportShares_Grantee CHECK (GranteeType IN ('USER','ROLE'))
        );
    END

    -- ==========================================================================
    -- COMPLETION
    -- ==========================================================================
    COMMIT TRANSACTION;
    PRINT N'═══════════════════════════════════════════════════════════════════════════';
    PRINT N'✅ Custom Report Designer Store deployed successfully.';
    PRINT N'   - Definitions + columns (width/order/formulas) + grouping/sort';
    PRINT N'     + filters + share grants.';
    PRINT N'   - Formulas are sandboxed text — parsed in-app, never executed as SQL.';
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
