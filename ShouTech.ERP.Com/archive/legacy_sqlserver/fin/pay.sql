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
            EndorsedTo NVARCHAR(200) NULL,               -- Endorsement (تظهير) to third party
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
            ('COD',          N'الدفع عند الاستلام',            'Cash on Delivery',      0,  0,  0),
            ('NET15',        N'صافي 15 يوم',                  'Net 15 Days',           15,  0,  0),
            ('NET30',        N'صافي 30 يوم',                  'Net 30 Days',           30,  0,  0),
            ('NET60',        N'صافي 60 يوم',                  'Net 60 Days',           60,  0,  0),
            ('NET90',        N'صافي 90 يوم',                  'Net 90 Days',           90,  0,  0),
            ('2-10-NET30',   N'خصم 2% خلال 10 أيام / صافي 30', '2/10 Net 30',           30,  2, 10)
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
            (1, 7,  N'تذكير ودي',
             N'عميلنا العزيز {CustomerName}، نذكّركم بأن الفاتورة بمبلغ {Amount} مستحقة منذ {DueDate}. نأمل التكرم بالسداد.', 1, 0, 0),
            (2, 15, N'إشعار ثانٍ',
             N'السيد/ة {CustomerName} المحترم/ة، رغم تذكيرنا السابق لا تزال الذمة بمبلغ {Amount} قائمة. نرجو السداد خلال 7 أيام.', 1, 1, 0),
            (3, 30, N'إشعار نهائي',
             N'إشعار نهائي: الذمة المستحقة على {CustomerName} بمبلغ {Amount} متأخرة. سيتم إيقاف البيع الآجل واتخاذ الإجراءات النظامية.', 1, 1, 1)
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
