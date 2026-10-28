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