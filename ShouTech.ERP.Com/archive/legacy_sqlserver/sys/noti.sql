-- ========================================================================
-- FILE: dba/sys/noti.sql
-- PROJECT: SHOUTECH ERP V10 - Notifications & Alerts Module (Ultimate 10/10)
-- VERSION: 10.3.0
-- DESCRIPTION: In-app notification center, user alert rules (low stock,
--              due invoices, license expiry), delivery tracking, and
--              offline-first queue for email/SMS dispatch when online.
-- ========================================================================
-- DESIGN PRINCIPLES:
-- ✅ Idempotent, TRY/CATCH + TRANSACTION wrapper.
-- ✅ Soft Delete + RowVersion + unified audit columns on ALL tables.
-- ✅ Offline-first: outbound queue stores messages; sync service dispatches.
-- ✅ No direct FKs (deferred to post.sql).
-- ========================================================================

USE [$(DbFileName)];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

PRINT N'═══════════════════════════════════════════════════════════════════════════';
PRINT N'SHOUTECH ERP V10 – Notifications & Alerts Module (Ultimate 10/10)';
PRINT N'═══════════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Notifications')
    BEGIN
        CREATE TABLE dbo.Notifications (
            NotificationID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            RecipientUserID INT NOT NULL,                -- Loose ref → Users
            NotificationType NVARCHAR(30) NOT NULL,      -- INFO | WARNING | CRITICAL | ACTION
            Category NVARCHAR(40) NOT NULL,              -- STOCK | INVOICE_DUE | LICENSE | BACKUP | SECURITY | SYSTEM
            TitleAR NVARCHAR(200) NOT NULL,
            TitleEN NVARCHAR(200) NOT NULL,
            BodyAR NVARCHAR(1000) NULL,
            BodyEN NVARCHAR(1000) NULL,
            SourceModule NVARCHAR(20) NULL,              -- INV | SALES | SYS | …
            SourceDocumentID BIGINT NULL,
            SourceDocumentNo NVARCHAR(50) NULL,
            ActionRoute NVARCHAR(200) NULL,              -- Navigation route on click
            Priority TINYINT NOT NULL DEFAULT 2,         -- 1 High … 3 Low
            IsRead BIT NOT NULL DEFAULT 0,
            ReadAt DATETIME2(7) NULL,
            IsDismissed BIT NOT NULL DEFAULT 0,
            ExpiresAt DATETIME2(7) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_Notifications PRIMARY KEY CLUSTERED (NotificationID),
            CONSTRAINT CK_Notifications_Type CHECK (NotificationType IN ('INFO','WARNING','CRITICAL','ACTION')),
            CONSTRAINT CK_Notifications_Priority CHECK (Priority BETWEEN 1 AND 3)
        ) WITH (DATA_COMPRESSION = PAGE);
        CREATE NONCLUSTERED INDEX IX_Notifications_Unread
            ON dbo.Notifications (CompanyID, RecipientUserID, IsRead, CreatedAt DESC)
            WHERE IsDeleted = 0 AND IsDismissed = 0;
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AlertRules')
    BEGIN
        CREATE TABLE dbo.AlertRules (
            AlertRuleID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            RuleCode NVARCHAR(40) NOT NULL,
            RuleNameAR NVARCHAR(150) NOT NULL,
            RuleNameEN NVARCHAR(150) NOT NULL,
            Category NVARCHAR(40) NOT NULL,
            TriggerCondition NVARCHAR(500) NOT NULL,     -- JSON: {"field":"QtyOnHand","op":"<","value":"ReorderLevel"}
            ScopeType NVARCHAR(10) NOT NULL DEFAULT 'ROLE',  -- ROLE | USER | ALL
            ScopeID INT NULL,                            -- RoleID or UserID (NULL for ALL)
            ThresholdValue DECIMAL(18,4) NULL,
            CheckFrequency NVARCHAR(10) NOT NULL DEFAULT 'DAILY', -- REALTIME | HOURLY | DAILY
            LastEvaluatedAt DATETIME2(7) NULL,
            Priority TINYINT NOT NULL DEFAULT 2,
            NotifyInApp BIT NOT NULL DEFAULT 1,
            NotifyEmail BIT NOT NULL DEFAULT 0,
            NotifySms BIT NOT NULL DEFAULT 0,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_AlertRules PRIMARY KEY CLUSTERED (AlertRuleID),
            CONSTRAINT UQ_AlertRules_Code UNIQUE (CompanyID, RuleCode),
            CONSTRAINT CK_AlertRules_Scope CHECK (ScopeType IN ('ROLE','USER','ALL')),
            CONSTRAINT CK_AlertRules_Frequency CHECK (CheckFrequency IN ('REALTIME','HOURLY','DAILY'))
        );
    END

    -- ==========================================================================
    --    The sync Windows Service drains this queue when connectivity exists.
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'OutboundMessages')
    BEGIN
        CREATE TABLE dbo.OutboundMessages (
            OutboundMessageID BIGINT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            Channel NVARCHAR(10) NOT NULL,               -- EMAIL | SMS | WEBHOOK
            RecipientAddress NVARCHAR(300) NOT NULL,     -- email/phone/URL
            Subject NVARCHAR(300) NULL,
            Body NVARCHAR(MAX) NOT NULL,
            BodyFormat NVARCHAR(10) NOT NULL DEFAULT 'TEXT', -- TEXT | HTML
            NotificationID BIGINT NULL,                  -- Source notification if any
            Status NVARCHAR(12) NOT NULL DEFAULT 'QUEUED', -- QUEUED | SENDING | SENT | FAILED | CANCELLED
            AttemptCount TINYINT NOT NULL DEFAULT 0,
            MaxAttempts TINYINT NOT NULL DEFAULT 5,
            NextAttemptAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            SentAt DATETIME2(7) NULL,
            LastError NVARCHAR(1000) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_OutboundMessages PRIMARY KEY CLUSTERED (OutboundMessageID),
            CONSTRAINT CK_OutboundMessages_Channel CHECK (Channel IN ('EMAIL','SMS','WEBHOOK')),
            CONSTRAINT CK_OutboundMessages_Status CHECK (Status IN ('QUEUED','SENDING','SENT','FAILED','CANCELLED')),
            CONSTRAINT CK_OutboundMessages_Format CHECK (BodyFormat IN ('TEXT','HTML'))
        ) WITH (DATA_COMPRESSION = PAGE);
        -- Index shaped exactly for the sync-service dequeue query
        CREATE NONCLUSTERED INDEX IX_OutboundMessages_Dequeue
            ON dbo.OutboundMessages (CompanyID, Status, NextAttemptAt)
            INCLUDE (Channel, RecipientAddress, AttemptCount) WHERE IsDeleted = 0;
    END

    -- ==========================================================================
    -- ==========================================================================
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'NotificationPreferences')
    BEGIN
        CREATE TABLE dbo.NotificationPreferences (
            PreferenceID INT IDENTITY(1,1) NOT NULL,
            CompanyID UNIQUEIDENTIFIER NOT NULL,
            UserID INT NOT NULL,
            Category NVARCHAR(40) NOT NULL,
            InAppEnabled BIT NOT NULL DEFAULT 1,
            EmailEnabled BIT NOT NULL DEFAULT 0,
            SmsEnabled BIT NOT NULL DEFAULT 0,
            QuietHoursFrom TIME(0) NULL,
            QuietHoursTo TIME(0) NULL,
            IsDeleted BIT NOT NULL DEFAULT 0,
            DeletedAt DATETIME2(7) NULL,
            DeletedBy UNIQUEIDENTIFIER NULL,
            CreatedBy UNIQUEIDENTIFIER NOT NULL,
            CreatedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            UpdatedBy UNIQUEIDENTIFIER NULL,
            UpdatedAt DATETIME2(7) NULL,
            RowVersion ROWVERSION,
            CONSTRAINT PK_NotificationPreferences PRIMARY KEY CLUSTERED (PreferenceID),
            CONSTRAINT UQ_NotificationPreferences UNIQUE (CompanyID, UserID, Category)
        );
    END

    -- ==========================================================================
    -- 5. SEED DATA — default alert rules
    -- ==========================================================================
    PRINT N'✅ Seeding default alert rules...';

    IF NOT EXISTS (SELECT 1 FROM dbo.AlertRules WHERE RuleCode = 'STOCK-LOW')
    BEGIN
        INSERT INTO dbo.AlertRules
            (CompanyID, RuleCode, RuleNameAR, RuleNameEN, Category, TriggerCondition,
             ScopeType, CheckFrequency, Priority, CreatedBy)
        SELECT TOP 1 C.CompanyID, X.Code, X.NameAR, X.NameEN, X.Cat, X.Cond,
               'ALL', X.Freq, X.Prio, '00000000-0000-0000-0000-000000000001'
        FROM MST.dbo.Companies C
        CROSS JOIN (VALUES
            ('STOCK-LOW',     N'انخفاض المخزون تحت حد الطلب',   'Stock below reorder level',   'STOCK',       N'{"field":"QtyOnHand","op":"<","value":"ReorderLevel"}', 'HOURLY',  1),
            ('INV-DUE-7',     N'فواتير مستحقة خلال 7 أيام',     'Invoices due within 7 days',  'INVOICE_DUE', N'{"field":"DueDate","op":"<=","value":"+7d"}',             'DAILY',   2),
            ('INV-OVERDUE',   N'فواتير متأخرة السداد',          'Overdue invoices',            'INVOICE_DUE', N'{"field":"DueDate","op":"<","value":"today"}',           'DAILY',   1),
            ('LIC-EXPIRE-30', N'الترخيص ينتهي خلال 30 يوماً',   'License expires in 30 days',  'LICENSE',     N'{"field":"LicenseExpiry","op":"<=","value":"+30d"}',     'DAILY',   1),
            ('BACKUP-FAIL',   N'فشل آخر نسخة احتياطية',         'Last backup failed',          'BACKUP',      N'{"field":"LastBackupStatus","op":"=","value":"FAILED"}', 'REALTIME',1),
            ('SEC-LOGIN-FAIL',N'محاولات دخول فاشلة متكررة',     'Repeated failed logins',      'SECURITY',    N'{"field":"FailedLoginCount","op":">=","value":5}',       'REALTIME',1)
        ) X (Code, NameAR, NameEN, Cat, Cond, Freq, Prio);
    END

    -- ==========================================================================
    -- COMPLETION
    -- ==========================================================================
    COMMIT TRANSACTION;
    PRINT N'═══════════════════════════════════════════════════════════════════════════';
    PRINT N'✅ Notifications & Alerts Module deployed successfully.';
    PRINT N'   - In-app center + alert rules + offline outbound queue + preferences.';
    PRINT N'   - Dequeue index shaped for the sync Windows Service.';
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
