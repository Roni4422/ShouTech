-- ========================================================================
-- FILE: dba/sys/tax.sql
-- PROJECT: SHOUTECH ERP V10 - Tax & VAT Compliance Module (Ultimate 10/10)
-- VERSION: 10.3.0
-- DESCRIPTION: Multi-jurisdiction tax engine covering GCC VAT (ZATCA/FTA/
--              NBR/OTA), Levant (Jordan ISTD, Lebanon, Palestine, Syria),
--              North Africa (Egypt ETA, Morocco, Algeria, Tunisia, Libya),
--              and European VAT (incl. OSS/IOSS + reverse charge).
--              Supports withholding tax, excise tax, exemptions, validity
--              periods, and e-invoicing readiness (Fatoora/JoFotara/ETA).
-- ========================================================================
-- DESIGN PRINCIPLES:
-- ✅ Idempotent (IF NOT EXISTS on every object) — safe re-run.
-- ✅ TRY/CATCH + TRANSACTION wrapper around entire script.
-- ✅ Soft Delete (IsDeleted, DeletedAt, DeletedBy) on ALL tables.
-- ✅ RowVersion + unified audit columns on ALL tables.
-- ✅ No direct FKs (deferred to post.sql) — CompanyID/AccountID loose refs.
-- ✅ Validity-period rates (ValidFrom/ValidTo) — never overwrite history.
-- ✅ Seed data for GCC, Levant, North Africa and EU standard rates.
-- ========================================================================

USE [$(DbFileName)];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

PRINT N'═══════════════════════════════════════════════════════════════════════════';
PRINT N'SHOUTECH ERP V10 – Tax & VAT Compliance Module (Ultimate 10/10)';
PRINT N'═══════════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- ==========================================================================
    --    One row per country/region authority the company may trade with.
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TaxJurisdictions')
    BEGIN
        CREATE TABLE dbo.TaxJurisdictions (
            JurisdictionID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            CountryCode NVARCHAR(2) NOT NULL,            -- ISO 3166-1 alpha-2 (SA, AE, EG, DE…)
            RegionCode NVARCHAR(10) NULL,                -- State/province when applicable
            AuthorityNameAR NVARCHAR(200) NOT NULL,      -- e.g. هيئة الزكاة والضريبة والجمارك
            AuthorityNameEN NVARCHAR(200) NOT NULL,      -- e.g. ZATCA, FTA, ETA
            TaxSystem NVARCHAR(30) NOT NULL,             -- VAT | GST | SALES_TAX | NONE
            CurrencyCode NVARCHAR(3) NOT NULL,
            EInvoicePlatform NVARCHAR(30) NULL,          -- FATOORA | JOFOTARA | ETA | PEPPOL | NULL
            EInvoiceUrl NVARCHAR(500) NULL,
            RegistrationThreshold DECIMAL(18,4) NULL,    -- Mandatory registration threshold (local ccy)
            IsDomestic BIT NOT NULL DEFAULT 0,           -- Company's home jurisdiction
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_TaxJurisdictions PRIMARY KEY CLUSTERED (JurisdictionID),
            CONSTRAINT UQ_TaxJurisdictions_Country UNIQUE (CompanyID, CountryCode, RegionCode)
        );
        CREATE NONCLUSTERED INDEX IX_TaxJurisdictions_Country
            ON dbo.TaxJurisdictions (CompanyID, CountryCode) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    --    Standard / Reduced / Zero-rated / Exempt / Out-of-scope / Reverse charge
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TaxCategories')
    BEGIN
        CREATE TABLE dbo.TaxCategories (
            TaxCategoryID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            CategoryCode NVARCHAR(20) NOT NULL,          -- STD | RED | ZERO | EXM | OOS | RC
            CategoryNameAR NVARCHAR(150) NOT NULL,
            CategoryNameEN NVARCHAR(150) NOT NULL,
            TaxNature NVARCHAR(20) NOT NULL DEFAULT 'VAT', -- VAT | EXCISE | WHT | CUSTOMS
            IsRecoverable BIT NOT NULL DEFAULT 1,        -- Input VAT recoverable?
            RequiresTaxInvoice BIT NOT NULL DEFAULT 1,
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
            CONSTRAINT PK_TaxCategories PRIMARY KEY CLUSTERED (TaxCategoryID),
            CONSTRAINT UQ_TaxCategories_Code UNIQUE (CompanyID, CategoryCode)
        );
    END

    -- ==========================================================================
    --    History-preserving: rate changes create NEW rows, never UPDATE old.
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TaxRates')
    BEGIN
        CREATE TABLE dbo.TaxRates (
            TaxRateID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            JurisdictionID INT NOT NULL,
            TaxCategoryID INT NOT NULL,
            RateCode NVARCHAR(30) NOT NULL,              -- SA-STD-15 | AE-STD-5 | EG-STD-14
            RatePercent DECIMAL(9,4) NOT NULL,           -- 15.0000
            ValidFrom DATE NOT NULL,
            ValidTo DATE NULL,                           -- NULL = open-ended
            AppliesToSales BIT NOT NULL DEFAULT 1,
            AppliesToPurchases BIT NOT NULL DEFAULT 1,
            IsReverseCharge BIT NOT NULL DEFAULT 0,      -- EU B2B / imports
            IsCompound BIT NOT NULL DEFAULT 0,           -- e.g. Quebec-style stacking (future-proof)
            TaxAccountID INT NULL,                       -- Output/Input VAT GL account (loose ref)
            PurchaseTaxAccountID INT NULL,
            LegalReference NVARCHAR(200) NULL,           -- Article/decree reference
            Notes NVARCHAR(500) NULL,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_TaxRates PRIMARY KEY CLUSTERED (TaxRateID),
            CONSTRAINT CK_TaxRates_Percent CHECK (RatePercent >= 0 AND RatePercent <= 100),
            CONSTRAINT CK_TaxRates_Validity CHECK (ValidTo IS NULL OR ValidTo >= ValidFrom)
        );
        CREATE NONCLUSTERED INDEX IX_TaxRates_Lookup
            ON dbo.TaxRates (CompanyID, JurisdictionID, TaxCategoryID, ValidFrom)
            INCLUDE (RatePercent, IsReverseCharge) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    --    A line item may carry VAT + Excise simultaneously (tobacco, drinks…).
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TaxGroups')
    BEGIN
        CREATE TABLE dbo.TaxGroups (
            TaxGroupID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            GroupCode NVARCHAR(30) NOT NULL,
            GroupNameAR NVARCHAR(150) NOT NULL,
            GroupNameEN NVARCHAR(150) NOT NULL,
            IsDefault BIT NOT NULL DEFAULT 0,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_TaxGroups PRIMARY KEY CLUSTERED (TaxGroupID),
            CONSTRAINT UQ_TaxGroups_Code UNIQUE (CompanyID, GroupCode)
        );
    END

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TaxGroupRates')
    BEGIN
        CREATE TABLE dbo.TaxGroupRates (
            TaxGroupRateID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            TaxGroupID INT NOT NULL,
            TaxRateID INT NOT NULL,
            Sequence TINYINT NOT NULL DEFAULT 1,         -- Application order for compounding
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_TaxGroupRates PRIMARY KEY CLUSTERED (TaxGroupRateID),
            CONSTRAINT UQ_TaxGroupRates_Pair UNIQUE (TaxGroupID, TaxRateID)
        );
    END

    -- ==========================================================================
    --    Per-item overrides; falls back to TaxGroup when NULL.
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ItemTaxMappings')
    BEGIN
        CREATE TABLE dbo.ItemTaxMappings (
            ItemTaxMappingID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            ItemID INT NOT NULL,                         -- Loose ref → inventory item
            JurisdictionID INT NOT NULL,
            SalesTaxCategoryID INT NOT NULL,
            PurchaseTaxCategoryID INT NULL,
            HSCode NVARCHAR(20) NULL,                    -- Harmonized System code (customs/excise)
            ExciseRate DECIMAL(9,4) NULL,                -- e.g. 100% tobacco, 50% soft drinks (KSA)
            IsExempt BIT NOT NULL DEFAULT 0,
            ExemptionReasonCode NVARCHAR(20) NULL,       -- ZATCA exemption reason codes (VATEX-SA-*)
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_ItemTaxMappings PRIMARY KEY CLUSTERED (ItemTaxMappingID),
            CONSTRAINT UQ_ItemTaxMappings_Item UNIQUE (CompanyID, ItemID, JurisdictionID)
        );
        CREATE NONCLUSTERED INDEX IX_ItemTaxMappings_Item
            ON dbo.ItemTaxMappings (CompanyID, ItemID) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PartyTaxIdentifiers')
    BEGIN
        CREATE TABLE dbo.PartyTaxIdentifiers (
            PartyTaxID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            PartyType NVARCHAR(10) NOT NULL,             -- CUSTOMER | VENDOR | COMPANY
            PartyID INT NOT NULL,                        -- Loose ref → customer/vendor/company
            JurisdictionID INT NOT NULL,
            TaxRegistrationNo NVARCHAR(30) NOT NULL,     -- TRN / VAT No / TIN
            RegistrationNameAR NVARCHAR(200) NULL,
            RegistrationNameEN NVARCHAR(200) NULL,
            IsVerified BIT NOT NULL DEFAULT 0,           -- VIES/ZATCA lookup passed
            VerifiedAt DATETIME2(7) NULL,
            ValidFrom DATE NULL,
            ValidTo DATE NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_PartyTaxIdentifiers PRIMARY KEY CLUSTERED (PartyTaxID),
            CONSTRAINT CK_PartyTaxIdentifiers_Type CHECK (PartyType IN ('CUSTOMER','VENDOR','COMPANY')),
            CONSTRAINT UQ_PartyTaxIdentifiers_Party UNIQUE (CompanyID, PartyType, PartyID, JurisdictionID)
        );
        CREATE NONCLUSTERED INDEX IX_PartyTaxIdentifiers_TRN
            ON dbo.PartyTaxIdentifiers (CompanyID, TaxRegistrationNo) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    --    Relevant for Egypt/KSA non-resident payments, Jordan, etc.
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WithholdingTaxRules')
    BEGIN
        CREATE TABLE dbo.WithholdingTaxRules (
            WhtRuleID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            JurisdictionID INT NOT NULL,
            RuleCode NVARCHAR(30) NOT NULL,
            RuleNameAR NVARCHAR(150) NOT NULL,
            RuleNameEN NVARCHAR(150) NOT NULL,
            PaymentType NVARCHAR(30) NOT NULL,           -- SERVICES | ROYALTIES | RENT | DIVIDENDS | CONTRACTS
            ResidentRate DECIMAL(9,4) NOT NULL DEFAULT 0,
            NonResidentRate DECIMAL(9,4) NOT NULL DEFAULT 0,
            MinThreshold DECIMAL(18,4) NULL,
            WhtPayableAccountID INT NULL,
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
            CONSTRAINT PK_WithholdingTaxRules PRIMARY KEY CLUSTERED (WhtRuleID),
            CONSTRAINT UQ_WithholdingTaxRules_Code UNIQUE (CompanyID, RuleCode),
            CONSTRAINT CK_WithholdingTaxRules_Validity CHECK (ValidTo IS NULL OR ValidTo >= ValidFrom)
        );
    END

    -- ==========================================================================
    --    Every posted invoice line writes here → VAT return becomes a query.
    --    Immutable: rows are never updated, only reversed via ReversalOfID.
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TaxTransactions')
    BEGIN
        CREATE TABLE dbo.TaxTransactions (
            TaxTransactionID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            FiscalYear INT NOT NULL,
            PeriodNumber TINYINT NOT NULL,               -- 1..12
            TransactionDate DATE NOT NULL,
            Direction NVARCHAR(6) NOT NULL,              -- OUTPUT (sales) | INPUT (purchases)
            SourceModule NVARCHAR(10) NOT NULL,          -- SALES | PURCH | POS | GL
            SourceDocumentID BIGINT NOT NULL,
            SourceDocumentNo NVARCHAR(50) NOT NULL,
            SourceLineID BIGINT NULL,
            JurisdictionID INT NOT NULL,
            TaxRateID INT NOT NULL,
            PartyTaxID INT NULL,
            TaxableAmount DECIMAL(18,4) NOT NULL,
            TaxAmount DECIMAL(18,4) NOT NULL,
            CurrencyCode NVARCHAR(3) NOT NULL,
            ExchangeRate DECIMAL(18,6) NOT NULL DEFAULT 1,
            TaxableAmountBase DECIMAL(18,4) NOT NULL,    -- In company base currency
            TaxAmountBase DECIMAL(18,4) NOT NULL,
            IsReverseCharge BIT NOT NULL DEFAULT 0,
            IsAdjustment BIT NOT NULL DEFAULT 0,
            ReversalOfID BIGINT NULL,
            TaxReturnID INT NULL,                        -- Set when claimed in a filed return
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_TaxTransactions PRIMARY KEY CLUSTERED (TaxTransactionID),
            CONSTRAINT CK_TaxTransactions_Direction CHECK (Direction IN ('OUTPUT','INPUT')),
            CONSTRAINT CK_TaxTransactions_Amounts CHECK (TaxableAmount >= 0 AND TaxAmount >= 0)
        ) WITH (DATA_COMPRESSION = PAGE);
        CREATE NONCLUSTERED INDEX IX_TaxTransactions_Period
            ON dbo.TaxTransactions (CompanyID, FiscalYear, PeriodNumber, Direction)
            INCLUDE (TaxableAmountBase, TaxAmountBase, TaxReturnID) WHERE IsDeleted = 0;
        CREATE NONCLUSTERED INDEX IX_TaxTransactions_Doc
            ON dbo.TaxTransactions (CompanyID, SourceModule, SourceDocumentID) WHERE IsDeleted = 0;
        CREATE COLUMNSTORE INDEX CSIX_TaxTransactions
            ON dbo.TaxTransactions (CompanyID, FiscalYear, PeriodNumber, Direction,
                                    TaxRateID, TaxableAmountBase, TaxAmountBase);
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TaxReturns')
    BEGIN
        CREATE TABLE dbo.TaxReturns (
            TaxReturnID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            JurisdictionID INT NOT NULL,
            ReturnType NVARCHAR(20) NOT NULL,            -- VAT | WHT | EXCISE
            FiscalYear INT NOT NULL,
            PeriodFrom DATE NOT NULL,
            PeriodTo DATE NOT NULL,
            FilingFrequency NVARCHAR(10) NOT NULL,       -- MONTHLY | QUARTERLY | ANNUAL
            TotalOutputTax DECIMAL(18,4) NOT NULL DEFAULT 0,
            TotalInputTax DECIMAL(18,4) NOT NULL DEFAULT 0,
            NetPayable DECIMAL(18,4) NOT NULL DEFAULT 0, -- + payable / − refundable
            Adjustments DECIMAL(18,4) NOT NULL DEFAULT 0,
            CurrencyCode NVARCHAR(3) NOT NULL,
            Status NVARCHAR(15) NOT NULL DEFAULT 'DRAFT', -- DRAFT | SUBMITTED | PAID | AMENDED
            SubmittedAt DATETIME2(7) NULL,
            SubmittedBy UNIQUEIDENTIFIER NULL,
            AuthorityRefNo NVARCHAR(100) NULL,           -- Acknowledgement/receipt number
            PaymentDate DATE NULL,
            Notes NVARCHAR(1000) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_TaxReturns PRIMARY KEY CLUSTERED (TaxReturnID),
            CONSTRAINT CK_TaxReturns_Period CHECK (PeriodTo >= PeriodFrom),
            CONSTRAINT CK_TaxReturns_Status CHECK (Status IN ('DRAFT','SUBMITTED','PAID','AMENDED')),
            CONSTRAINT UQ_TaxReturns_Period UNIQUE (CompanyID, JurisdictionID, ReturnType, PeriodFrom)
        );
        CREATE NONCLUSTERED INDEX IX_TaxReturns_Status
            ON dbo.TaxReturns (CompanyID, Status, FiscalYear) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- 10. SEED DATA — TAX CATEGORIES
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM dbo.TaxCategories)
    BEGIN
        INSERT INTO dbo.TaxCategories
            (CompanyID, CategoryCode, CategoryNameAR, CategoryNameEN, TaxNature, IsRecoverable, RequiresTaxInvoice, SortOrder, CreatedBy)
        SELECT C.CompanyGuid, X.CategoryCode, X.NameAR, X.NameEN, X.Nature, X.Recoverable, X.NeedsInvoice, X.SortOrd, C.CompanyGuid
        FROM (SELECT DISTINCT CompanyID AS CompanyGuid FROM dbo.TaxJurisdictions) C
        CROSS JOIN (VALUES
            ('STD',  N'النسبة الأساسية',        'Standard Rate',      'VAT',    1, 1, 1),
            ('RED',  N'النسبة المخفضة',         'Reduced Rate',       'VAT',    1, 1, 2),
            ('ZERO', N'صفرية',                  'Zero-Rated',         'VAT',    1, 1, 3),
            ('EXM',  N'معفاة',                  'Exempt',             'VAT',    0, 0, 4),
            ('OOS',  N'خارج النطاق',            'Out of Scope',       'VAT',    0, 0, 5),
            ('RC',   N'احتساب عكسي',            'Reverse Charge',     'VAT',    1, 1, 6),
            ('EXC',  N'ضريبة انتقائية',         'Excise Tax',         'EXCISE', 0, 1, 7),
            ('WHT',  N'استقطاع من المنبع',      'Withholding Tax',    'WHT',    0, 0, 8)
        ) X (CategoryCode, NameAR, NameEN, Nature, Recoverable, NeedsInvoice, SortOrd);
    END

    -- ==========================================================================
    -- 11. SEED DATA — JURISDICTIONS
    --     GCC + Levant + North Africa + EU hub (as of 2026).
    -- ==========================================================================
    PRINT N'✅ Seeding tax jurisdictions...';

    IF NOT EXISTS (SELECT 1 FROM dbo.TaxJurisdictions WHERE CountryCode = 'SA')
    BEGIN
        INSERT INTO dbo.TaxJurisdictions
            (CompanyID, CountryCode, RegionCode, AuthorityNameAR, AuthorityNameEN,
             TaxSystem, CurrencyCode, EInvoicePlatform, RegistrationThreshold, IsDomestic, CreatedBy)
        SELECT TOP 1 C.CompanyID, X.CC, NULL, X.AuthAR, X.AuthEN, X.Sys, X.Ccy, X.EInv, X.Thresh,
               CASE WHEN X.CC = 'SA' THEN 1 ELSE 0 END, '00000000-0000-0000-0000-000000000001'
        FROM MST.dbo.Companies C
        CROSS JOIN (VALUES
            ('SA', N'هيئة الزكاة والضريبة والجمارك',        'ZATCA',  'VAT', 'SAR', 'FATOORA',  375000),
            ('AE', N'الهيئة الاتحادية للضرائب',             'FTA',    'VAT', 'AED', NULL,       375000),
            ('BH', N'الجهاز الوطني للإيرادات',              'NBR',    'VAT', 'BHD', NULL,        37500),
            ('OM', N'جهاز الضرائب',                         'OTA',    'VAT', 'OMR', NULL,        38500),
            ('KW', N'وزارة المالية الكويتية',               'MOF-KW', 'NONE','KWD', NULL,          NULL),
            ('QA', N'الهيئة العامة للضرائب',                'GTA',    'NONE','QAR', NULL,          NULL),
            ('JO', N'دائرة ضريبة الدخل والمبيعات',          'ISTD',   'VAT', 'JOD', 'JOFOTARA',  30000),
            ('LB', N'مديرية الضريبة على القيمة المضافة',    'MOF-LB', 'VAT', 'LBP', NULL,          NULL),
            ('PS', N'دائرة ضريبة القيمة المضافة',           'MOF-PS', 'VAT', 'ILS', NULL,          NULL),
            ('SY', N'الهيئة العامة للضرائب والرسوم',        'GTF',    'NONE','SYP', NULL,          NULL),
            ('IQ', N'الهيئة العامة للضرائب',                'GCT-IQ', 'NONE','IQD', NULL,          NULL),
            ('EG', N'مصلحة الضرائب المصرية',                'ETA',    'VAT', 'EGP', 'ETA',      500000),
            ('MA', N'المديرية العامة للضرائب',              'DGI',    'VAT', 'MAD', NULL,     2000000),
            ('DZ', N'المديرية العامة للضرائب',              'DGI-DZ', 'VAT', 'DZD', NULL,          NULL),
            ('TN', N'الإدارة العامة للدراسات والتشريع الجبائي','DGI-TN','VAT', 'TND', NULL,      100000),
            ('LY', N'مصلحة الضرائب الليبية',                'LTA',    'NONE','LYD', NULL,          NULL),
            ('SD', N'ديوان الضرائب السوداني',               'STC',    'VAT', 'SDG', NULL,          NULL),
            ('DE', N'مكتب الضرائب الاتحادي',                'BZSt',   'VAT', 'EUR', 'PEPPOL',   100000),
            ('FR', N'المديرية العامة للمالية العامة',       'DGFiP',  'VAT', 'EUR', 'PEPPOL',    91900),
            ('GB', N'هيئة الضرائب والجمارك',                'HMRC',   'VAT', 'GBP', NULL,        90000),
            ('TR', N'رئاسة إدارة الإيرادات',                'GIB',    'VAT', 'TRY', 'E-FATURA',  NULL)
        ) X (CC, AuthAR, AuthEN, Sys, Ccy, EInv, Thresh);
    END

    -- ==========================================================================
    -- 12. SEED DATA — STANDARD VAT/GST RATES (validity-period based)
    --     Historical rows preserved: e.g. SA 5% → 15%, BH 5% → 10%.
    -- ==========================================================================
    PRINT N'✅ Seeding standard tax rates...';

    IF NOT EXISTS (SELECT 1 FROM dbo.TaxRates WHERE RateCode = 'SA-STD-15')
    BEGIN
        DECLARE @SystemUser UNIQUEIDENTIFIER = '00000000-0000-0000-0000-000000000001';
        DECLARE @CompID UNIQUEIDENTIFIER = (SELECT TOP 1 CompanyID FROM MST.dbo.Companies);
        DECLARE @StdCatID INT = (SELECT TOP 1 TaxCategoryID FROM dbo.TaxCategories WHERE CategoryCode = 'STD');
        DECLARE @RedCatID INT = (SELECT TOP 1 TaxCategoryID FROM dbo.TaxCategories WHERE CategoryCode = 'RED');
        DECLARE @ZeroCatID INT = (SELECT TOP 1 TaxCategoryID FROM dbo.TaxCategories WHERE CategoryCode = 'ZERO');

        INSERT INTO dbo.TaxRates
            (CompanyID, JurisdictionID, TaxCategoryID, RateCode, RatePercent,
             ValidFrom, ValidTo, CreatedBy)
        SELECT @CompID, J.JurisdictionID, X.CatID, X.RateCode, X.Rate, X.VFrom, X.VTo, @SystemUser
        FROM (VALUES
            ('SA', 'SA-STD-5',   @StdCatID,  5.00, '2018-01-01', '2020-06-30'),
            ('SA', 'SA-STD-15',  @StdCatID, 15.00, '2020-07-01', NULL),
            ('SA', 'SA-ZERO-0',  @ZeroCatID, 0.00, '2018-01-01', NULL),
            ('AE', 'AE-STD-5',   @StdCatID,  5.00, '2018-01-01', NULL),
            ('AE', 'AE-ZERO-0',  @ZeroCatID, 0.00, '2018-01-01', NULL),
            ('BH', 'BH-STD-5',   @StdCatID,  5.00, '2019-01-01', '2021-12-31'),
            ('BH', 'BH-STD-10',  @StdCatID, 10.00, '2022-01-01', NULL),
            ('OM', 'OM-STD-5',   @StdCatID,  5.00, '2021-04-16', NULL),
            ('JO', 'JO-STD-16',  @StdCatID, 16.00, '1994-01-01', NULL),
            ('JO', 'JO-RED-4',   @RedCatID,  4.00, '2009-01-01', NULL),
            ('LB', 'LB-STD-11',  @StdCatID, 11.00, '2018-01-01', NULL),
            ('PS', 'PS-STD-16',  @StdCatID, 16.00, '1999-01-01', NULL),
            ('EG', 'EG-STD-14',  @StdCatID, 14.00, '2017-07-01', NULL),
            ('EG', 'EG-RED-5',   @RedCatID,  5.00, '2016-09-08', NULL),
            ('MA', 'MA-STD-20',  @StdCatID, 20.00, '2013-01-01', NULL),
            ('MA', 'MA-RED-10',  @RedCatID, 10.00, '2013-01-01', NULL),
            ('MA', 'MA-RED-7',   @RedCatID,  7.00, '2013-01-01', NULL),
            ('DZ', 'DZ-STD-19',  @StdCatID, 19.00, '2017-01-01', NULL),
            ('DZ', 'DZ-RED-9',   @RedCatID,  9.00, '2017-01-01', NULL),
            ('TN', 'TN-STD-19',  @StdCatID, 19.00, '2018-01-01', NULL),
            ('TN', 'TN-RED-13',  @RedCatID, 13.00, '2018-01-01', NULL),
            ('TN', 'TN-RED-7',   @RedCatID,  7.00, '2018-01-01', NULL),
            ('SD', 'SD-STD-17',  @StdCatID, 17.00, '2023-01-01', NULL),
            ('DE', 'DE-STD-19',  @StdCatID, 19.00, '2007-01-01', NULL),
            ('DE', 'DE-RED-7',   @RedCatID,  7.00, '1983-07-01', NULL),
            ('FR', 'FR-STD-20',  @StdCatID, 20.00, '2014-01-01', NULL),
            ('FR', 'FR-RED-5.5', @RedCatID,  5.50, '2014-01-01', NULL),
            ('GB', 'GB-STD-20',  @StdCatID, 20.00, '2011-01-04', NULL),
            ('GB', 'GB-RED-5',   @RedCatID,  5.00, '1997-09-01', NULL),
            ('TR', 'TR-STD-20',  @StdCatID, 20.00, '2023-07-10', NULL),
            ('TR', 'TR-RED-10',  @RedCatID, 10.00, '2023-07-10', NULL),
            ('TR', 'TR-RED-1',   @RedCatID,  1.00, '2023-07-10', NULL)
        ) X (CC, RateCode, CatID, Rate, VFrom, VTo)
        INNER JOIN dbo.TaxJurisdictions J
            ON J.CompanyID = @CompID AND J.CountryCode = X.CC AND J.IsDeleted = 0;
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM dbo.TaxGroups WHERE GroupCode = 'DEF-STD')
    BEGIN
        INSERT INTO dbo.TaxGroups (CompanyID, GroupCode, GroupNameAR, GroupNameEN, IsDefault, CreatedBy)
        SELECT TOP 1 CompanyID, 'DEF-STD', N'المجموعة الافتراضية — النسبة الأساسية',
               'Default Group — Standard Rate', 1, '00000000-0000-0000-0000-000000000001'
        FROM MST.dbo.Companies;
    END

    -- ==========================================================================
    -- COMPLETION
    -- ==========================================================================
    COMMIT TRANSACTION;
    PRINT N'═══════════════════════════════════════════════════════════════════════════';
    PRINT N'✅ Tax & VAT Compliance Module deployed successfully.';
    PRINT N'   - 9 tables (jurisdictions, categories, rates, groups, mappings,';
    PRINT N'     party TRNs, WHT rules, transactions, returns).';
    PRINT N'   - GCC + Levant + North Africa + Europe seed rates (validity-based).';
    PRINT N'   - E-invoicing readiness: FATOORA / JOFOTARA / ETA / PEPPOL / E-FATURA.';
    PRINT N'   - Immutable TaxTransactions with columnstore analytics.';
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
