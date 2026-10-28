-- ============================================================================
-- File: dba/04_SALES.sql
-- Module: Customers, Sales Invoices, E-Invoicing (ZATCA Phase 2 Ready) & Returns
-- Grade: Ultra Enterprise (UUIDs, QR Hashes, Row Integrity, Performance Indexes)
-- ============================================================================

CREATE TABLE CustomerGroups (
    GroupID INT IDENTITY(1,1) PRIMARY KEY,
    GroupNameAr NVARCHAR(100) NOT NULL,
    DiscountRate DECIMAL(5,2) DEFAULT 0.00,
    CreditLimit DECIMAL(18,4) DEFAULT 0.0000
);

CREATE TABLE Customers (
    CustomerID INT IDENTITY(1,1) PRIMARY KEY,
    CustomerCode NVARCHAR(20) NOT NULL UNIQUE,
    BranchID INT NOT NULL FOREIGN KEY REFERENCES Branches(BranchID),
    GroupID INT NULL FOREIGN KEY REFERENCES CustomerGroups(GroupID),
    NameAr NVARCHAR(200) NOT NULL,
    NameEn NVARCHAR(200) NULL,
    TaxNumber NVARCHAR(20) NULL, -- الرقم الضريبي للمنشآت
    CommercialRegister NVARCHAR(30) NULL,
    Phone NVARCHAR(30) NULL,
    Email NVARCHAR(100) NULL,
    Address NVARCHAR(300) NULL,
    CreditLimit DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    PaymentTermsDays INT DEFAULT 0, -- فترة السداد بالأيام
    AccountID INT NOT NULL FOREIGN KEY REFERENCES Accounts(AccountID), -- حساب الذمم المدينة
    IsActive BIT DEFAULT 1,
    IsDeleted BIT DEFAULT 0,
    CreatedAt DATETIME2 DEFAULT GETDATE(),
    CreatedBy INT NOT NULL FOREIGN KEY REFERENCES Users(UserID)
);

CREATE TABLE SalesInvoices (
    InvoiceID BIGINT IDENTITY(1,1) PRIMARY KEY,
    InvoiceUUID UNIQUEIDENTIFIER DEFAULT NEWID() NOT NULL UNIQUE, -- معرف فريد عالمي للفوترة
    InvoiceNo NVARCHAR(30) NOT NULL UNIQUE,
    InvoiceType NVARCHAR(20) NOT NULL DEFAULT 'Standard', -- Standard (B2B), Simplified (B2C)
    BranchID INT NOT NULL FOREIGN KEY REFERENCES Branches(BranchID),
    FiscalYearID INT NOT NULL FOREIGN KEY REFERENCES FiscalYears(FiscalYearID),
    CustomerID INT NOT NULL FOREIGN KEY REFERENCES Customers(CustomerID),
    CurrencyID INT NOT NULL FOREIGN KEY REFERENCES Currencies(CurrencyID),
    ExchangeRate DECIMAL(18,6) NOT NULL DEFAULT 1.000000,
    InvoiceDate DATE NOT NULL,
    DueDate DATE NULL,
    
    SubTotal DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    TotalDiscount DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    TaxableAmount DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    TotalTax DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    GrandTotal DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    PaidAmount DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    RemainingAmount AS (GrandTotal - PaidAmount),

    ZATCA_QRCode NVARCHAR(MAX) NULL,
    ZATCA_InvoiceHash NVARCHAR(256) NULL,
    ZATCA_Status NVARCHAR(30) DEFAULT 'Pending', -- Pending, Reported, Cleared, Rejected

    Status NVARCHAR(20) NOT NULL DEFAULT 'Draft', -- Draft, Approved, Posted, Void
    JournalHeaderID BIGINT NULL FOREIGN KEY REFERENCES JournalHeaders(JournalHeaderID),
    IsDeleted BIT DEFAULT 0,
    CreatedBy INT NOT NULL FOREIGN KEY REFERENCES Users(UserID),
    ApprovedBy INT NULL FOREIGN KEY REFERENCES Users(UserID),
    CreatedAt DATETIME2 DEFAULT GETDATE(),
    UpdatedAt DATETIME2 NULL
);

CREATE TABLE SalesInvoiceLines (
    LineID BIGINT IDENTITY(1,1) PRIMARY KEY,
    InvoiceID BIGINT NOT NULL FOREIGN KEY REFERENCES SalesInvoices(InvoiceID) ON DELETE CASCADE,
    ProductID BIGINT NOT NULL, -- يرتبط بجدول الأصناف Products
    WarehouseID INT NOT NULL,  -- يرتبط بجدول المستودعات Warehouses
    Quantity DECIMAL(18,4) NOT NULL CHECK (Quantity > 0),
    UnitPrice DECIMAL(18,4) NOT NULL CHECK (UnitPrice >= 0),
    DiscountRate DECIMAL(5,2) DEFAULT 0.00,
    DiscountAmount DECIMAL(18,4) DEFAULT 0.0000,
    TaxRate DECIMAL(5,2) NOT NULL DEFAULT 15.00,
    TaxAmount DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    LineTotal DECIMAL(18,4) NOT NULL, -- الإجمالي شامل الضريبة
    LineNo INT NOT NULL
);

CREATE TABLE SalesReturns (
    ReturnID BIGINT IDENTITY(1,1) PRIMARY KEY,
    ReturnUUID UNIQUEIDENTIFIER DEFAULT NEWID() NOT NULL UNIQUE,
    ReturnNo NVARCHAR(30) NOT NULL UNIQUE,
    OriginalInvoiceID BIGINT NOT NULL FOREIGN KEY REFERENCES SalesInvoices(InvoiceID),
    BranchID INT NOT NULL FOREIGN KEY REFERENCES Branches(BranchID),
    CustomerID INT NOT NULL FOREIGN KEY REFERENCES Customers(CustomerID),
    ReturnDate DATE NOT NULL,
    SubTotal DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    TotalTax DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    GrandTotal DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    Reason NVARCHAR(300) NULL,
    JournalHeaderID BIGINT NULL FOREIGN KEY REFERENCES JournalHeaders(JournalHeaderID),
    CreatedBy INT NOT NULL FOREIGN KEY REFERENCES Users(UserID),
    CreatedAt DATETIME2 DEFAULT GETDATE()
);

CREATE INDEX IX_SalesInvoices_InvoiceDate ON SalesInvoices(InvoiceDate);
CREATE INDEX IX_SalesInvoices_CustomerID ON SalesInvoices(CustomerID);
CREATE INDEX IX_SalesInvoices_ZATCA_Status ON SalesInvoices(ZATCA_Status);
CREATE INDEX IX_SalesInvoiceLines_InvoiceID ON SalesInvoiceLines(InvoiceID);