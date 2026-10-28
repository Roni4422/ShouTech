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