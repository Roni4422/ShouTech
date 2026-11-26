-- ============================================================
-- source: archive/legacy_sqlserver/root/01_SEC.sql
-- ============================================================
-- ============================================================================
-- File: dba/01_SEC.sql (Enterprise Grade)
-- ============================================================================

CREATE TABLE Companies (
    CompanyID INT IDENTITY(1,1) PRIMARY KEY,
    CompanyNameAr NVARCHAR(200) NOT NULL,
    CompanyNameEn NVARCHAR(200) NULL,
    TaxNumber NVARCHAR(20) NOT NULL,
    CommercialRegister NVARCHAR(30) NULL,
    LogoPath NVARCHAR(500) NULL,
    IsActive BIT DEFAULT 1,
    IsDeleted BIT DEFAULT 0, -- Soft Delete
    CreatedAt DATETIME2 DEFAULT GETDATE(),
    CreatedBy INT NULL
);

CREATE TABLE Branches (
    BranchID INT IDENTITY(1,1) PRIMARY KEY,
    CompanyID INT NOT NULL FOREIGN KEY REFERENCES Companies(CompanyID),
    BranchCode NVARCHAR(20) NOT NULL UNIQUE,
    BranchNameAr NVARCHAR(150) NOT NULL,
    BranchNameEn NVARCHAR(150) NULL,
    Phone NVARCHAR(30) NULL,
    Address NVARCHAR(300) NULL,
    IsActive BIT DEFAULT 1,
    IsDeleted BIT DEFAULT 0
);

CREATE TABLE Roles (
    RoleID INT IDENTITY(1,1) PRIMARY KEY,
    RoleName NVARCHAR(50) NOT NULL UNIQUE,
    Description NVARCHAR(250) NULL
);

CREATE TABLE Users (
    UserID INT IDENTITY(1,1) PRIMARY KEY,
    BranchID INT NOT NULL FOREIGN KEY REFERENCES Branches(BranchID),
    Username NVARCHAR(50) NOT NULL UNIQUE,
    PasswordHash NVARCHAR(256) NOT NULL,
    FullName NVARCHAR(150) NOT NULL,
    Email NVARCHAR(100) NULL,
    IsActive BIT DEFAULT 1,
    IsDeleted BIT DEFAULT 0,
    LastLoginAt DATETIME2 NULL,
    CreatedAt DATETIME2 DEFAULT GETDATE()
);

CREATE TABLE UserRoles (
    UserID INT NOT NULL FOREIGN KEY REFERENCES Users(UserID),
    RoleID INT NOT NULL FOREIGN KEY REFERENCES Roles(RoleID),
    PRIMARY KEY (UserID, RoleID)
);

CREATE TABLE SystemAuditLogs (
    LogID BIGINT IDENTITY(1,1) PRIMARY KEY,
    UserID INT NULL FOREIGN KEY REFERENCES Users(UserID),
    ActionType NVARCHAR(50) NOT NULL, -- INSERT, UPDATE, DELETE, LOGIN
    TableName NVARCHAR(100) NOT NULL,
    RecordID NVARCHAR(50) NOT NULL,
    OldValues NVARCHAR(MAX) NULL, -- JSON Representation
    NewValues NVARCHAR(MAX) NULL, -- JSON Representation
    IPAddress NVARCHAR(45) NULL,
    Timestamp DATETIME2 DEFAULT GETDATE()
);

-- ============================================================
-- source: archive/legacy_sqlserver/root/02_GL_COA.sql
-- ============================================================
-- ============================================================================
-- File: dba/02_GL_COA.sql (Enterprise Grade)
-- ============================================================================

CREATE TABLE Currencies (
    CurrencyID INT IDENTITY(1,1) PRIMARY KEY,
    CurrencyCode NVARCHAR(5) NOT NULL UNIQUE, -- SAR, USD, KWD
    CurrencyNameAr NVARCHAR(50) NOT NULL,
    ExchangeRate DECIMAL(18,6) NOT NULL DEFAULT 1.000000,
    IsBaseCurrency BIT DEFAULT 0
);

CREATE TABLE Accounts (
    AccountID INT IDENTITY(1,1) PRIMARY KEY,
    AccountCode NVARCHAR(30) NOT NULL UNIQUE,
    AccountNameAr NVARCHAR(200) NOT NULL,
    AccountNameEn NVARCHAR(200) NULL,
    ParentAccountID INT NULL FOREIGN KEY REFERENCES Accounts(AccountID),
    AccountType NVARCHAR(30) NOT NULL,
    AccountLevel INT NOT NULL,
    IsSubAccount BIT NOT NULL DEFAULT 1,
    AllowDirectPosting BIT DEFAULT 1,
    IsActive BIT NOT NULL DEFAULT 1,
    IsDeleted BIT NOT NULL DEFAULT 0
);

CREATE TABLE FiscalYears (
    FiscalYearID INT IDENTITY(1,1) PRIMARY KEY,
    BranchID INT NOT NULL FOREIGN KEY REFERENCES Branches(BranchID),
    YearName NVARCHAR(50) NOT NULL,
    StartDate DATE NOT NULL,
    EndDate DATE NOT NULL,
    IsClosed BIT DEFAULT 0,
    ClosedBy INT NULL FOREIGN KEY REFERENCES Users(UserID),
    ClosedAt DATETIME2 NULL
);

CREATE TABLE CostCenters (
    CostCenterID INT IDENTITY(1,1) PRIMARY KEY,
    CostCenterCode NVARCHAR(20) NOT NULL UNIQUE,
    CostCenterNameAr NVARCHAR(150) NOT NULL,
    ParentID INT NULL FOREIGN KEY REFERENCES CostCenters(CostCenterID),
    IsActive BIT DEFAULT 1
);

CREATE TABLE JournalHeaders (
    JournalHeaderID BIGINT IDENTITY(1,1) PRIMARY KEY,
    BranchID INT NOT NULL FOREIGN KEY REFERENCES Branches(BranchID),
    FiscalYearID INT NOT NULL FOREIGN KEY REFERENCES FiscalYears(FiscalYearID),
    CurrencyID INT NOT NULL FOREIGN KEY REFERENCES Currencies(CurrencyID),
    ExchangeRate DECIMAL(18,6) NOT NULL DEFAULT 1.000000,
    JournalNo NVARCHAR(30) NOT NULL UNIQUE,
    PostingDate DATE NOT NULL,
    DocReference NVARCHAR(100) NULL,
    SourceModule NVARCHAR(30) NOT NULL,
    Description NVARCHAR(500) NULL,
    TotalDebit DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    TotalCredit DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    Status NVARCHAR(20) NOT NULL DEFAULT 'Draft', -- Draft, PendingApproval, Posted, Void
    CreatedBy INT NOT NULL FOREIGN KEY REFERENCES Users(UserID),
    ApprovedBy INT NULL FOREIGN KEY REFERENCES Users(UserID),
    CreatedAt DATETIME2 DEFAULT GETDATE(),
    UpdatedAt DATETIME2 NULL,
    CONSTRAINT CK_JournalHeader_Balance CHECK (TotalDebit = TotalCredit)
);

CREATE TABLE JournalLines (
    JournalLineID BIGINT IDENTITY(1,1) PRIMARY KEY,
    JournalHeaderID BIGINT NOT NULL FOREIGN KEY REFERENCES JournalHeaders(JournalHeaderID) ON DELETE CASCADE,
    AccountID INT NOT NULL FOREIGN KEY REFERENCES Accounts(AccountID),
    CostCenterID INT NULL FOREIGN KEY REFERENCES CostCenters(CostCenterID),
    Debit DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    Credit DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    DebitFC DECIMAL(18,4) NOT NULL DEFAULT 0.0000, 
    CreditFC DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    LineDescription NVARCHAR(300) NULL,
    LineNo INT NOT NULL,
    CONSTRAINT CK_JournalLine_DebitCredit CHECK ((Debit > 0 AND Credit = 0) OR (Credit > 0 AND Debit = 0))
);

-- ============================================================================
-- ============================================================================
CREATE INDEX IX_JournalHeaders_PostingDate ON JournalHeaders(PostingDate);
CREATE INDEX IX_JournalHeaders_BranchID ON JournalHeaders(BranchID);
CREATE INDEX IX_JournalLines_AccountID ON JournalLines(AccountID);
CREATE INDEX IX_JournalLines_JournalHeaderID ON JournalLines(JournalHeaderID);

-- ============================================================
-- source: archive/legacy_sqlserver/root/03_CASH.sql
-- ============================================================
-- ============================================================================
-- File: dba/03_CASH.sql
-- Module: Cash, Banking, Payment/Receipt Vouchers & Check Management
-- Grade: Ultra Enterprise (Multi-Currency, Multi-Branch, Approval Workflows)
-- ============================================================================

CREATE TABLE Banks (
    BankID INT IDENTITY(1,1) PRIMARY KEY,
    BranchID INT NOT NULL FOREIGN KEY REFERENCES Branches(BranchID),
    BankNameAr NVARCHAR(150) NOT NULL,
    BankNameEn NVARCHAR(150) NULL,
    AccountNo NVARCHAR(50) NOT NULL,
    IBAN NVARCHAR(50) NULL,
    SWIFTCode NVARCHAR(20) NULL,
    AccountID INT NOT NULL FOREIGN KEY REFERENCES Accounts(AccountID), -- link to the chart of accounts
    IsActive BIT DEFAULT 1,
    IsDeleted BIT DEFAULT 0,
    CreatedAt DATETIME2 DEFAULT GETDATE(),
    CreatedBy INT NOT NULL FOREIGN KEY REFERENCES Users(UserID)
);

CREATE TABLE PaymentMethods (
    PaymentMethodID INT IDENTITY(1,1) PRIMARY KEY,
    MethodNameAr NVARCHAR(50) NOT NULL,
    MethodNameEn NVARCHAR(50) NULL,
    Type NVARCHAR(20) NOT NULL, -- Cash, Bank, Card, Check
    DefaultAccountID INT NULL FOREIGN KEY REFERENCES Accounts(AccountID),
    IsActive BIT DEFAULT 1
);

CREATE TABLE ReceiptVouchers (
    VoucherID BIGINT IDENTITY(1,1) PRIMARY KEY,
    VoucherNo NVARCHAR(30) NOT NULL UNIQUE,
    BranchID INT NOT NULL FOREIGN KEY REFERENCES Branches(BranchID),
    FiscalYearID INT NOT NULL FOREIGN KEY REFERENCES FiscalYears(FiscalYearID),
    VoucherDate DATE NOT NULL,
    CurrencyID INT NOT NULL FOREIGN KEY REFERENCES Currencies(CurrencyID),
    ExchangeRate DECIMAL(18,6) NOT NULL DEFAULT 1.000000,
    PayeeName NVARCHAR(200) NOT NULL, -- payee name
    AccountID INT NOT NULL FOREIGN KEY REFERENCES Accounts(AccountID), -- debit account (cash/bank)
    PaymentMethodID INT NOT NULL FOREIGN KEY REFERENCES PaymentMethods(PaymentMethodID),
    TotalAmount DECIMAL(18,4) NOT NULL CHECK (TotalAmount > 0),
    TotalAmountFC DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    Description NVARCHAR(500) NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT 'Draft', -- Draft, Approved, Posted, Void
    JournalHeaderID BIGINT NULL FOREIGN KEY REFERENCES JournalHeaders(JournalHeaderID),
    IsDeleted BIT DEFAULT 0,
    CreatedBy INT NOT NULL FOREIGN KEY REFERENCES Users(UserID),
    ApprovedBy INT NULL FOREIGN KEY REFERENCES Users(UserID),
    CreatedAt DATETIME2 DEFAULT GETDATE(),
    UpdatedAt DATETIME2 NULL
);

CREATE TABLE ReceiptVoucherLines (
    LineID BIGINT IDENTITY(1,1) PRIMARY KEY,
    VoucherID BIGINT NOT NULL FOREIGN KEY REFERENCES ReceiptVouchers(VoucherID) ON DELETE CASCADE,
    AccountID INT NOT NULL FOREIGN KEY REFERENCES Accounts(AccountID), -- credit account
    CostCenterID INT NULL FOREIGN KEY REFERENCES CostCenters(CostCenterID),
    Amount DECIMAL(18,4) NOT NULL CHECK (Amount > 0),
    AmountFC DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    Memo NVARCHAR(300) NULL,
    LineNo INT NOT NULL
);

CREATE TABLE PaymentVouchers (
    VoucherID BIGINT IDENTITY(1,1) PRIMARY KEY,
    VoucherNo NVARCHAR(30) NOT NULL UNIQUE,
    BranchID INT NOT NULL FOREIGN KEY REFERENCES Branches(BranchID),
    FiscalYearID INT NOT NULL FOREIGN KEY REFERENCES FiscalYears(FiscalYearID),
    VoucherDate DATE NOT NULL,
    CurrencyID INT NOT NULL FOREIGN KEY REFERENCES Currencies(CurrencyID),
    ExchangeRate DECIMAL(18,6) NOT NULL DEFAULT 1.000000,
    PayeeName NVARCHAR(200) NOT NULL, -- recipient name
    AccountID INT NOT NULL FOREIGN KEY REFERENCES Accounts(AccountID), -- credit account (cash/bank)
    PaymentMethodID INT NOT NULL FOREIGN KEY REFERENCES PaymentMethods(PaymentMethodID),
    TotalAmount DECIMAL(18,4) NOT NULL CHECK (TotalAmount > 0),
    TotalAmountFC DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    Description NVARCHAR(500) NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT 'Draft',
    JournalHeaderID BIGINT NULL FOREIGN KEY REFERENCES JournalHeaders(JournalHeaderID),
    IsDeleted BIT DEFAULT 0,
    CreatedBy INT NOT NULL FOREIGN KEY REFERENCES Users(UserID),
    ApprovedBy INT NULL FOREIGN KEY REFERENCES Users(UserID),
    CreatedAt DATETIME2 DEFAULT GETDATE(),
    UpdatedAt DATETIME2 NULL
);

CREATE TABLE PaymentVoucherLines (
    LineID BIGINT IDENTITY(1,1) PRIMARY KEY,
    VoucherID BIGINT NOT NULL FOREIGN KEY REFERENCES PaymentVouchers(VoucherID) ON DELETE CASCADE,
    AccountID INT NOT NULL FOREIGN KEY REFERENCES Accounts(AccountID), -- debit account
    CostCenterID INT NULL FOREIGN KEY REFERENCES CostCenters(CostCenterID),
    Amount DECIMAL(18,4) NOT NULL CHECK (Amount > 0),
    AmountFC DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    Memo NVARCHAR(300) NULL,
    LineNo INT NOT NULL
);

CREATE TABLE Checks (
    CheckID BIGINT IDENTITY(1,1) PRIMARY KEY,
    CheckNo NVARCHAR(50) NOT NULL,
    BankID INT NOT NULL FOREIGN KEY REFERENCES Banks(BankID),
    CheckDate DATE NOT NULL,
    DueDate DATE NOT NULL,
    Amount DECIMAL(18,4) NOT NULL CHECK (Amount > 0),
    Type NVARCHAR(10) NOT NULL, -- Inward (received), Outward (paid)
    Status NVARCHAR(20) NOT NULL DEFAULT 'UnderCollection', -- UnderCollection, Cleared, Bounced, Cancelled
    ReceiptVoucherID BIGINT NULL FOREIGN KEY REFERENCES ReceiptVouchers(VoucherID),
    PaymentVoucherID BIGINT NULL FOREIGN KEY REFERENCES PaymentVouchers(VoucherID),
    JournalHeaderID BIGINT NULL FOREIGN KEY REFERENCES JournalHeaders(JournalHeaderID)
);

CREATE INDEX IX_ReceiptVouchers_Date ON ReceiptVouchers(VoucherDate);
CREATE INDEX IX_PaymentVouchers_Date ON PaymentVouchers(VoucherDate);
CREATE INDEX IX_Checks_Status ON Checks(Status);

-- ============================================================
-- source: archive/legacy_sqlserver/root/04_SALES.sql
-- ============================================================
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
    TaxNumber NVARCHAR(20) NULL, -- establishment tax number
    CommercialRegister NVARCHAR(30) NULL,
    Phone NVARCHAR(30) NULL,
    Email NVARCHAR(100) NULL,
    Address NVARCHAR(300) NULL,
    CreditLimit DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    PaymentTermsDays INT DEFAULT 0, -- payment period in days
    AccountID INT NOT NULL FOREIGN KEY REFERENCES Accounts(AccountID), -- Accounts Receivable account
    IsActive BIT DEFAULT 1,
    IsDeleted BIT DEFAULT 0,
    CreatedAt DATETIME2 DEFAULT GETDATE(),
    CreatedBy INT NOT NULL FOREIGN KEY REFERENCES Users(UserID)
);

CREATE TABLE SalesInvoices (
    InvoiceID BIGINT IDENTITY(1,1) PRIMARY KEY,
    InvoiceUUID UNIQUEIDENTIFIER DEFAULT NEWID() NOT NULL UNIQUE, -- globally unique invoice identifier
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
    ProductID BIGINT NOT NULL, -- links to the Products table
    WarehouseID INT NOT NULL,  -- links to the Warehouses table
    Quantity DECIMAL(18,4) NOT NULL CHECK (Quantity > 0),
    UnitPrice DECIMAL(18,4) NOT NULL CHECK (UnitPrice >= 0),
    DiscountRate DECIMAL(5,2) DEFAULT 0.00,
    DiscountAmount DECIMAL(18,4) DEFAULT 0.0000,
    TaxRate DECIMAL(5,2) NOT NULL DEFAULT 15.00,
    TaxAmount DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    LineTotal DECIMAL(18,4) NOT NULL, -- total including tax
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

-- ============================================================
-- source: archive/legacy_sqlserver/root/05_PURCH.sql
-- ============================================================
-- ============================================================================
-- File: dba/05_PURCH.sql
-- Module: Vendors, Purchase Requisitions, Orders, GRN & Purchase Invoices
-- Architecture Grade: Hyperscale Enterprise (3-Way Matching & Landed Cost Ready)
-- ============================================================================

CREATE TABLE VendorGroups (
    GroupID INT IDENTITY(1,1) PRIMARY KEY,
    GroupNameAr NVARCHAR(100) NOT NULL,
    GroupNameEn NVARCHAR(100) NULL,
    DefaultPaymentTermsDays INT DEFAULT 30
);

CREATE TABLE Vendors (
    VendorID INT IDENTITY(1,1) PRIMARY KEY,
    VendorCode NVARCHAR(20) NOT NULL UNIQUE,
    BranchID INT NOT NULL FOREIGN KEY REFERENCES Branches(BranchID),
    GroupID INT NULL FOREIGN KEY REFERENCES VendorGroups(GroupID),
    NameAr NVARCHAR(200) NOT NULL,
    NameEn NVARCHAR(200) NULL,
    TaxNumber NVARCHAR(20) NULL,
    CommercialRegister NVARCHAR(30) NULL,
    Phone NVARCHAR(30) NULL,
    Email NVARCHAR(100) NULL,
    Address NVARCHAR(300) NULL,
    BankIBAN NVARCHAR(50) NULL,
    AccountID INT NOT NULL FOREIGN KEY REFERENCES Accounts(AccountID), -- accounts payable account
    IsActive BIT DEFAULT 1,
    IsDeleted BIT DEFAULT 0,
    CreatedAt DATETIME2 DEFAULT GETDATE(),
    CreatedBy INT NOT NULL FOREIGN KEY REFERENCES Users(UserID)
);

CREATE TABLE PurchaseRequisitions (
    RequisitionID BIGINT IDENTITY(1,1) PRIMARY KEY,
    RequisitionNo NVARCHAR(30) NOT NULL UNIQUE,
    BranchID INT NOT NULL FOREIGN KEY REFERENCES Branches(BranchID),
    RequestDate DATE NOT NULL,
    RequiredDate DATE NOT NULL,
    Department NVARCHAR(100) NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT 'Draft', -- Draft, PendingApproval, Approved, Rejected, ConvertedToPO
    Priority NVARCHAR(15) DEFAULT 'Medium', -- Low, Medium, High, Urgent
    Notes NVARCHAR(500) NULL,
    CreatedBy INT NOT NULL FOREIGN KEY REFERENCES Users(UserID),
    ApprovedBy INT NULL FOREIGN KEY REFERENCES Users(UserID),
    CreatedAt DATETIME2 DEFAULT GETDATE()
);

CREATE TABLE PurchaseRequisitionLines (
    LineID BIGINT IDENTITY(1,1) PRIMARY KEY,
    RequisitionID BIGINT NOT NULL FOREIGN KEY REFERENCES PurchaseRequisitions(RequisitionID) ON DELETE CASCADE,
    ProductID BIGINT NOT NULL,
    RequestedQty DECIMAL(18,4) NOT NULL CHECK (RequestedQty > 0),
    ApprovedQty DECIMAL(18,4) DEFAULT 0.0000,
    Notes NVARCHAR(200) NULL,
    LineNo INT NOT NULL
);

CREATE TABLE PurchaseOrders (
    POID BIGINT IDENTITY(1,1) PRIMARY KEY,
    PONumber NVARCHAR(30) NOT NULL UNIQUE,
    RequisitionID BIGINT NULL FOREIGN KEY REFERENCES PurchaseRequisitions(RequisitionID),
    BranchID INT NOT NULL FOREIGN KEY REFERENCES Branches(BranchID),
    VendorID INT NOT NULL FOREIGN KEY REFERENCES Vendors(VendorID),
    CurrencyID INT NOT NULL FOREIGN KEY REFERENCES Currencies(CurrencyID),
    ExchangeRate DECIMAL(18,6) NOT NULL DEFAULT 1.000000,
    PODate DATE NOT NULL,
    ExpectedDeliveryDate DATE NULL,
    PaymentTerms NVARCHAR(200) NULL,
    SubTotal DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    TotalTax DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    GrandTotal DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    Status NVARCHAR(20) NOT NULL DEFAULT 'Draft', -- Draft, Approved, PartiallyReceived, FullyReceived, Cancelled
    IsClosed BIT DEFAULT 0,
    CreatedBy INT NOT NULL FOREIGN KEY REFERENCES Users(UserID),
    ApprovedBy INT NULL FOREIGN KEY REFERENCES Users(UserID),
    CreatedAt DATETIME2 DEFAULT GETDATE()
);

CREATE TABLE PurchaseOrderLines (
    POLineID BIGINT IDENTITY(1,1) PRIMARY KEY,
    POID BIGINT NOT NULL FOREIGN KEY REFERENCES PurchaseOrders(POID) ON DELETE CASCADE,
    ProductID BIGINT NOT NULL,
    OrderedQty DECIMAL(18,4) NOT NULL CHECK (OrderedQty > 0),
    ReceivedQty DECIMAL(18,4) DEFAULT 0.0000,
    UnitPrice DECIMAL(18,4) NOT NULL CHECK (UnitPrice >= 0),
    TaxRate DECIMAL(5,2) DEFAULT 15.00,
    TaxAmount DECIMAL(18,4) DEFAULT 0.0000,
    LineTotal DECIMAL(18,4) NOT NULL,
    LineNo INT NOT NULL
);

CREATE TABLE GoodsReceiptNotes (
    GRNID BIGINT IDENTITY(1,1) PRIMARY KEY,
    GRNNumber NVARCHAR(30) NOT NULL UNIQUE,
    POID BIGINT NULL FOREIGN KEY REFERENCES PurchaseOrders(POID),
    BranchID INT NOT NULL FOREIGN KEY REFERENCES Branches(BranchID),
    VendorID INT NOT NULL FOREIGN KEY REFERENCES Vendors(VendorID),
    WarehouseID INT NOT NULL,
    ReceiptDate DATE NOT NULL,
    VendorDeliveryNoteNo NVARCHAR(50) NULL, -- vendor external delivery note number
    Status NVARCHAR(20) DEFAULT 'Received', -- Received, Inspected, Billed
    CreatedBy INT NOT NULL FOREIGN KEY REFERENCES Users(UserID),
    CreatedAt DATETIME2 DEFAULT GETDATE()
);

CREATE TABLE GoodsReceiptNoteLines (
    GRNLineID BIGINT IDENTITY(1,1) PRIMARY KEY,
    GRNID BIGINT NOT NULL FOREIGN KEY REFERENCES GoodsReceiptNotes(GRNID) ON DELETE CASCADE,
    ProductID BIGINT NOT NULL,
    ReceivedQty DECIMAL(18,4) NOT NULL CHECK (ReceivedQty > 0),
    BatchNo NVARCHAR(50) NULL,
    ExpiryDate DATE NULL,
    UnitCost DECIMAL(18,4) NOT NULL,
    LineNo INT NOT NULL
);

CREATE TABLE PurchaseInvoices (
    InvoiceID BIGINT IDENTITY(1,1) PRIMARY KEY,
    InvoiceNo NVARCHAR(30) NOT NULL UNIQUE,
    VendorInvoiceNo NVARCHAR(50) NOT NULL, -- original vendor invoice number
    VendorID INT NOT NULL FOREIGN KEY REFERENCES Vendors(VendorID),
    BranchID INT NOT NULL FOREIGN KEY REFERENCES Branches(BranchID),
    FiscalYearID INT NOT NULL FOREIGN KEY REFERENCES FiscalYears(FiscalYearID),
    GRNID BIGINT NULL FOREIGN KEY REFERENCES GoodsReceiptNotes(GRNID),
    POID BIGINT NULL FOREIGN KEY REFERENCES PurchaseOrders(POID),
    CurrencyID INT NOT NULL FOREIGN KEY REFERENCES Currencies(CurrencyID),
    ExchangeRate DECIMAL(18,6) NOT NULL DEFAULT 1.000000,
    InvoiceDate DATE NOT NULL,
    DueDate DATE NOT NULL,
    
    SubTotal DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    TotalDiscount DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    TotalTax DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    LandedCostAmount DECIMAL(18,4) DEFAULT 0.0000, -- distributed costs
    GrandTotal DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    PaidAmount DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    
    Status NVARCHAR(20) NOT NULL DEFAULT 'Draft',
    JournalHeaderID BIGINT NULL FOREIGN KEY REFERENCES JournalHeaders(JournalHeaderID),
    CreatedBy INT NOT NULL FOREIGN KEY REFERENCES Users(UserID),
    ApprovedBy INT NULL FOREIGN KEY REFERENCES Users(UserID),
    CreatedAt DATETIME2 DEFAULT GETDATE()
);

CREATE TABLE PurchaseInvoiceLines (
    LineID BIGINT IDENTITY(1,1) PRIMARY KEY,
    InvoiceID BIGINT NOT NULL FOREIGN KEY REFERENCES PurchaseInvoices(InvoiceID) ON DELETE CASCADE,
    ProductID BIGINT NOT NULL,
    WarehouseID INT NOT NULL,
    Quantity DECIMAL(18,4) NOT NULL CHECK (Quantity > 0),
    UnitPrice DECIMAL(18,4) NOT NULL,
    LandedCostPerUnit DECIMAL(18,4) DEFAULT 0.0000, -- unit share of landed costs
    EffectiveUnitCost AS (UnitPrice + LandedCostPerUnit), -- total effective cost
    TaxRate DECIMAL(5,2) DEFAULT 15.00,
    TaxAmount DECIMAL(18,4) DEFAULT 0.0000,
    LineTotal DECIMAL(18,4) NOT NULL,
    LineNo INT NOT NULL
);

CREATE INDEX IX_PurchaseOrders_VendorID ON PurchaseOrders(VendorID);
CREATE INDEX IX_PurchaseInvoices_InvoiceDate ON PurchaseInvoices(InvoiceDate);
CREATE INDEX IX_GoodsReceiptNotes_POID ON GoodsReceiptNotes(POID);

-- ============================================================
-- source: archive/legacy_sqlserver/root/06_INV.sql
-- ============================================================
-- ============================================================================
-- File: dba/06_INV.sql
-- Module: Inventory, Multi-Warehouse, Bins, Batches, Serials & Landed Costs
-- Architecture Grade: Hyperscale Enterprise (Real-time Valuation & Traceability)
-- ============================================================================

CREATE TABLE UnitsOfMeasure (
    UOMID INT IDENTITY(1,1) PRIMARY KEY,
    UOMCode NVARCHAR(10) NOT NULL UNIQUE, -- PCS, BOX, KG, MTR
    UOMNameAr NVARCHAR(50) NOT NULL,
    UOMNameEn NVARCHAR(50) NULL
);

CREATE TABLE UOMConversions (
    ConversionID INT IDENTITY(1,1) PRIMARY KEY,
    FromUOMID INT NOT NULL FOREIGN KEY REFERENCES UnitsOfMeasure(UOMID),
    ToUOMID INT NOT NULL FOREIGN KEY REFERENCES UnitsOfMeasure(UOMID),
    ConversionFactor DECIMAL(18,6) NOT NULL CHECK (ConversionFactor > 0),
    CONSTRAINT UQ_UOMConversion UNIQUE (FromUOMID, ToUOMID)
);

CREATE TABLE Categories (
    CategoryID INT IDENTITY(1,1) PRIMARY KEY,
    CategoryCode NVARCHAR(20) NOT NULL UNIQUE,
    CategoryNameAr NVARCHAR(150) NOT NULL,
    CategoryNameEn NVARCHAR(150) NULL,
    ParentCategoryID INT NULL FOREIGN KEY REFERENCES Categories(CategoryID),
    IsActive BIT DEFAULT 1
);

CREATE TABLE Products (
    ProductID BIGINT IDENTITY(1,1) PRIMARY KEY,
    ProductCode NVARCHAR(50) NOT NULL UNIQUE,
    ProductNameAr NVARCHAR(250) NOT NULL,
    ProductNameEn NVARCHAR(250) NULL,
    CategoryID INT NOT NULL FOREIGN KEY REFERENCES Categories(CategoryID),
    BaseUOMID INT NOT NULL FOREIGN KEY REFERENCES UnitsOfMeasure(UOMID),
    
    CostingMethod NVARCHAR(20) NOT NULL DEFAULT 'AVCO', -- AVCO (moving average), FIFO, Standard
    TrackingType NVARCHAR(20) NOT NULL DEFAULT 'None', -- None, Batch, Serial
    
    StandardCost DECIMAL(18,4) DEFAULT 0.0000,
    MovingAverageCost DECIMAL(18,4) DEFAULT 0.0000,
    SellingPrice DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    MinSellingPrice DECIMAL(18,4) DEFAULT 0.0000,
    
    ReorderLevel DECIMAL(18,4) DEFAULT 0.0000,
    MaxStockLevel DECIMAL(18,4) DEFAULT 0.0000,
    TaxRate DECIMAL(5,2) DEFAULT 15.00,
    
    InventoryAccountID INT NOT NULL FOREIGN KEY REFERENCES Accounts(AccountID),
    COGSAccountID INT NOT NULL FOREIGN KEY REFERENCES Accounts(AccountID), -- Cost of Goods Sold account
    SalesAccountID INT NOT NULL FOREIGN KEY REFERENCES Accounts(AccountID),
    
    IsActive BIT DEFAULT 1,
    IsDeleted BIT DEFAULT 0,
    CreatedAt DATETIME2 DEFAULT GETDATE(),
    CreatedBy INT NOT NULL FOREIGN KEY REFERENCES Users(UserID)
);

CREATE TABLE ProductBarcodes (
    BarcodeID BIGINT IDENTITY(1,1) PRIMARY KEY,
    ProductID BIGINT NOT NULL FOREIGN KEY REFERENCES Products(ProductID) ON DELETE CASCADE,
    Barcode NVARCHAR(100) NOT NULL UNIQUE,
    UOMID INT NOT NULL FOREIGN KEY REFERENCES UnitsOfMeasure(UOMID),
    IsPrimary BIT DEFAULT 0
);

CREATE TABLE Warehouses (
    WarehouseID INT IDENTITY(1,1) PRIMARY KEY,
    WarehouseCode NVARCHAR(20) NOT NULL UNIQUE,
    BranchID INT NOT NULL FOREIGN KEY REFERENCES Branches(BranchID),
    WarehouseNameAr NVARCHAR(150) NOT NULL,
    WarehouseNameEn NVARCHAR(150) NULL,
    IsActive BIT DEFAULT 1
);

CREATE TABLE WarehouseBins (
    BinID INT IDENTITY(1,1) PRIMARY KEY,
    WarehouseID INT NOT NULL FOREIGN KEY REFERENCES Warehouses(WarehouseID),
    BinCode NVARCHAR(30) NOT NULL, -- e.g. A1-R02-S04
    ZoneName NVARCHAR(50) NULL,
    IsActive BIT DEFAULT 1,
    CONSTRAINT UQ_WarehouseBin UNIQUE (WarehouseID, BinCode)
);

CREATE TABLE ProductBatches (
    BatchID BIGINT IDENTITY(1,1) PRIMARY KEY,
    ProductID BIGINT NOT NULL FOREIGN KEY REFERENCES Products(ProductID),
    WarehouseID INT NOT NULL FOREIGN KEY REFERENCES Warehouses(WarehouseID),
    BatchNo NVARCHAR(50) NOT NULL,
    ProductionDate DATE NULL,
    ExpiryDate DATE NOT NULL,
    QuantityOnHand DECIMAL(18,4) NOT NULL DEFAULT 0.0000,
    IsLocked BIT DEFAULT 0,
    CONSTRAINT UQ_ProductBatch UNIQUE (ProductID, WarehouseID, BatchNo)
);

CREATE TABLE ProductSerials (
    SerialID BIGINT IDENTITY(1,1) PRIMARY KEY,
    ProductID BIGINT NOT NULL FOREIGN KEY REFERENCES Products(ProductID),
    WarehouseID INT NOT NULL FOREIGN KEY REFERENCES Warehouses(WarehouseID),
    SerialNumber NVARCHAR(100) NOT NULL UNIQUE,
    Status NVARCHAR(20) DEFAULT 'InStock', -- InStock, Sold, Scrapped, Returned
    InboundDate DATETIME2 DEFAULT GETDATE()
);

CREATE TABLE StockTransfers (
    TransferID BIGINT IDENTITY(1,1) PRIMARY KEY,
    TransferNo NVARCHAR(30) NOT NULL UNIQUE,
    FromWarehouseID INT NOT NULL FOREIGN KEY REFERENCES Warehouses(WarehouseID),
    ToWarehouseID INT NOT NULL FOREIGN KEY REFERENCES Warehouses(WarehouseID),
    TransferDate DATE NOT NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT 'Pending', -- Pending, InTransit, Received, Cancelled
    Notes NVARCHAR(300) NULL,
    CreatedBy INT NOT NULL FOREIGN KEY REFERENCES Users(UserID),
    ReceivedBy INT NULL FOREIGN KEY REFERENCES Users(UserID),
    CreatedAt DATETIME2 DEFAULT GETDATE()
);

CREATE TABLE StockTransferLines (
    LineID BIGINT IDENTITY(1,1) PRIMARY KEY,
    TransferID BIGINT NOT NULL FOREIGN KEY REFERENCES StockTransfers(TransferID) ON DELETE CASCADE,
    ProductID BIGINT NOT NULL FOREIGN KEY REFERENCES Products(ProductID),
    Quantity DECIMAL(18,4) NOT NULL CHECK (Quantity > 0),
    BatchNo NVARCHAR(50) NULL,
    LineNo INT NOT NULL
);

CREATE TABLE StockAdjustments (
    AdjustmentID BIGINT IDENTITY(1,1) PRIMARY KEY,
    AdjustmentNo NVARCHAR(30) NOT NULL UNIQUE,
    WarehouseID INT NOT NULL FOREIGN KEY REFERENCES Warehouses(WarehouseID),
    AdjustmentDate DATE NOT NULL,
    Reason NVARCHAR(250) NOT NULL,
    JournalHeaderID BIGINT NULL FOREIGN KEY REFERENCES JournalHeaders(JournalHeaderID),
    Status NVARCHAR(20) DEFAULT 'Draft', -- Draft, Approved
    CreatedBy INT NOT NULL FOREIGN KEY REFERENCES Users(UserID),
    ApprovedBy INT NULL FOREIGN KEY REFERENCES Users(UserID),
    CreatedAt DATETIME2 DEFAULT GETDATE()
);

CREATE TABLE StockAdjustmentLines (
    LineID BIGINT IDENTITY(1,1) PRIMARY KEY,
    AdjustmentID BIGINT NOT NULL FOREIGN KEY REFERENCES StockAdjustments(AdjustmentID) ON DELETE CASCADE,
    ProductID BIGINT NOT NULL FOREIGN KEY REFERENCES Products(ProductID),
    SystemQty DECIMAL(18,4) NOT NULL, -- system quantity
    PhysicalQty DECIMAL(18,4) NOT NULL, -- physical counted quantity
    VarianceQty AS (PhysicalQty - SystemQty), -- variance (+ or -)
    UnitCost DECIMAL(18,4) NOT NULL,
    TotalVarianceValue AS ((PhysicalQty - SystemQty) * UnitCost),
    LineNo INT NOT NULL
);

CREATE TABLE LandedCostAllocations (
    AllocationID BIGINT IDENTITY(1,1) PRIMARY KEY,
    PurchaseInvoiceID BIGINT NOT NULL FOREIGN KEY REFERENCES PurchaseInvoices(InvoiceID),
    ExpenseType NVARCHAR(100) NOT NULL, -- Freight, Customs, Insurance, Handling
    ExpenseAmount DECIMAL(18,4) NOT NULL CHECK (ExpenseAmount > 0),
    AllocationMethod NVARCHAR(30) NOT NULL DEFAULT 'ByValue', -- ByValue, ByQuantity, ByWeight
    CreatedBy INT NOT NULL FOREIGN KEY REFERENCES Users(UserID),
    CreatedAt DATETIME2 DEFAULT GETDATE()
);

CREATE INDEX IX_Products_CategoryID ON Products(CategoryID);
CREATE INDEX IX_ProductBarcodes_Barcode ON ProductBarcodes(Barcode);
CREATE INDEX IX_ProductBatches_ExpiryDate ON ProductBatches(ExpiryDate);
CREATE INDEX IX_ProductSerials_SerialNumber ON ProductSerials(SerialNumber);
