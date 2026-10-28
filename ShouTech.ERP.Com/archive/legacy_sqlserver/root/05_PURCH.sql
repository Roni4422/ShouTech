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
    AccountID INT NOT NULL FOREIGN KEY REFERENCES Accounts(AccountID), -- حساب الذمم الدائنة
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
    VendorDeliveryNoteNo NVARCHAR(50) NULL, -- رقم إذن التوريد الخارجي الخاص بالمورد
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
    VendorInvoiceNo NVARCHAR(50) NOT NULL, -- رقم فاتورة المورد الأصلية
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
    LandedCostAmount DECIMAL(18,4) DEFAULT 0.0000, -- المصاريف الموزعة
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
    LandedCostPerUnit DECIMAL(18,4) DEFAULT 0.0000, -- نصيب الوحدة من المصاريف الإضافية
    EffectiveUnitCost AS (UnitPrice + LandedCostPerUnit), -- التكلفة الفعلية الشاملة
    TaxRate DECIMAL(5,2) DEFAULT 15.00,
    TaxAmount DECIMAL(18,4) DEFAULT 0.0000,
    LineTotal DECIMAL(18,4) NOT NULL,
    LineNo INT NOT NULL
);

CREATE INDEX IX_PurchaseOrders_VendorID ON PurchaseOrders(VendorID);
CREATE INDEX IX_PurchaseInvoices_InvoiceDate ON PurchaseInvoices(InvoiceDate);
CREATE INDEX IX_GoodsReceiptNotes_POID ON GoodsReceiptNotes(POID);