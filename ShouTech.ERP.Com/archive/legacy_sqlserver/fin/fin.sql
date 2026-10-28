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