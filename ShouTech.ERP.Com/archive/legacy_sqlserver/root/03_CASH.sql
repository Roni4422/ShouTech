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
    AccountID INT NOT NULL FOREIGN KEY REFERENCES Accounts(AccountID), -- الربط بالشجرة المحاسبية
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
    PayeeName NVARCHAR(200) NOT NULL, -- اسم المسلّم
    AccountID INT NOT NULL FOREIGN KEY REFERENCES Accounts(AccountID), -- الحساب المدين (الخزينة/البنك)
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
    AccountID INT NOT NULL FOREIGN KEY REFERENCES Accounts(AccountID), -- الحساب الدائن
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
    PayeeName NVARCHAR(200) NOT NULL, -- اسم المستلم
    AccountID INT NOT NULL FOREIGN KEY REFERENCES Accounts(AccountID), -- الحساب الدائن (الخزينة/البنك)
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
    AccountID INT NOT NULL FOREIGN KEY REFERENCES Accounts(AccountID), -- الحساب المدين
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
    Type NVARCHAR(10) NOT NULL, -- Inward (مقبوض), Outward (مدفوع)
    Status NVARCHAR(20) NOT NULL DEFAULT 'UnderCollection', -- UnderCollection, Cleared, Bounced, Cancelled
    ReceiptVoucherID BIGINT NULL FOREIGN KEY REFERENCES ReceiptVouchers(VoucherID),
    PaymentVoucherID BIGINT NULL FOREIGN KEY REFERENCES PaymentVouchers(VoucherID),
    JournalHeaderID BIGINT NULL FOREIGN KEY REFERENCES JournalHeaders(JournalHeaderID)
);

CREATE INDEX IX_ReceiptVouchers_Date ON ReceiptVouchers(VoucherDate);
CREATE INDEX IX_PaymentVouchers_Date ON PaymentVouchers(VoucherDate);
CREATE INDEX IX_Checks_Status ON Checks(Status);