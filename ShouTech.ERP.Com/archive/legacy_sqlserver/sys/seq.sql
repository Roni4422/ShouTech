-- ═════════════════════════════════════════════════════════════════════════════════
-- FILE: dba/sys/seq.sql
-- SYSTEM: ShouTech ERP Enterprise Core
-- MODULE: Thread-Safe Sequence & Auto-Numbering Engine
-- GRADE: Enterprise Production Standard (Tier-1)
-- ═════════════════════════════════════════════════════════════════════════════════

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

RAISERROR(N'🚀 [ENTERPRISE BUILD] Initializing Sequence Engine (dba/sys/seq.sql)...', 0, 1) WITH NOWAIT;

BEGIN TRANSACTION;

BEGIN TRY

    IF OBJECT_ID(N'sys.DocumentSequences', N'U') IS NULL
    BEGIN
        CREATE TABLE sys.DocumentSequences (
            SequenceID          INT IDENTITY(1,1) NOT NULL,
            TenantID            INT NOT NULL DEFAULT 1,
            CompanyID           INT NOT NULL DEFAULT 1,
            SequenceCode        VARCHAR(50) NOT NULL,    -- e.g., 'SEQ_JOURNAL_ENTRY', 'SEQ_SALES_INVOICE'
            FiscalYear          INT NOT NULL,            -- السنة المالية
            Prefix              VARCHAR(20) NOT NULL,    -- e.g., 'JV-', 'INV-2026-'
            Suffix              VARCHAR(20) NULL,
            PaddingLength       INT NOT NULL DEFAULT 6,  -- length of zeros e.g. 000001
            CurrentValue        BIGINT NOT NULL DEFAULT 0,
            IncrementBy         INT NOT NULL DEFAULT 1,
            LastGeneratedNo     VARCHAR(100) NULL,
            RowVersion          ROWVERSION NOT NULL,

            CONSTRAINT PK_sys_DocumentSequences PRIMARY KEY CLUSTERED (SequenceID),
            CONSTRAINT UQ_sys_DocumentSequences UNIQUE (TenantID, CompanyID, SequenceCode, FiscalYear)
        );
    END;

    DECLARE @CurrYear INT = YEAR(SYSDATETIME());

    MERGE INTO sys.DocumentSequences AS Target
    USING (VALUES 
        (1, 1, 'SEQ_JOURNAL_ENTRY', @CurrYear, 'JV-', NULL, 6, 0, 1),
        (1, 1, 'SEQ_PAYMENT_VOUCHER', @CurrYear, 'PV-', NULL, 6, 0, 1),
        (1, 1, 'SEQ_RECEIPT_VOUCHER', @CurrYear, 'RV-', NULL, 6, 0, 1),
        (1, 1, 'SEQ_SALES_INVOICE',  @CurrYear, 'INV-', NULL, 6, 0, 1)
    ) AS Source (TenantID, CompanyID, SequenceCode, FiscalYear, Prefix, Suffix, PaddingLength, CurrentValue, IncrementBy)
    ON (Target.TenantID = Source.TenantID AND Target.CompanyID = Source.CompanyID AND Target.SequenceCode = Source.SequenceCode AND Target.FiscalYear = Source.FiscalYear)
    WHEN NOT MATCHED THEN
        INSERT (TenantID, CompanyID, SequenceCode, FiscalYear, Prefix, Suffix, PaddingLength, CurrentValue, IncrementBy)
        VALUES (Source.TenantID, Source.CompanyID, Source.SequenceCode, Source.FiscalYear, Source.Prefix, Source.Suffix, Source.PaddingLength, Source.CurrentValue, Source.IncrementBy);

    COMMIT TRANSACTION;
    RAISERROR(N'✅ [SUCCESS] Document Sequences table created and seeded.', 0, 1) WITH NOWAIT;

END TRY
BEGIN CATCH
    IF (XACT_STATE()) <> 0 ROLLBACK TRANSACTION;
    DECLARE @Err NVARCHAR(4000) = ERROR_MESSAGE();
    RAISERROR(N'❌ [FATAL ERROR] seq.sql failed: %s', 16, 1, @Err);
END CATCH;
GO

-- ═════════════════════════════════════════════════════════════════════════════════
-- ═════════════════════════════════════════════════════════════════════════════════
CREATE OR ALTER PROCEDURE sys.sp_GetNextSequenceValue
    @TenantID       INT = 1,
    @CompanyID      INT = 1,
    @SequenceCode   VARCHAR(50),
    @FiscalYear     INT,
    @GeneratedNo    VARCHAR(100) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @NextVal TABLE (NewValue BIGINT, Prefix VARCHAR(20), Suffix VARCHAR(20), PaddingLength INT);

    UPDATE sys.DocumentSequences WITH (UPDLOCK, ROWLOCK)
    SET CurrentValue = CurrentValue + IncrementBy
    OUTPUT INSERTED.CurrentValue, INSERTED.Prefix, INSERTED.Suffix, INSERTED.PaddingLength INTO @NextVal
    WHERE TenantID = @TenantID 
      AND CompanyID = @CompanyID 
      AND SequenceCode = @SequenceCode 
      AND FiscalYear = @FiscalYear;

    IF NOT EXISTS (SELECT 1 FROM @NextVal)
    BEGIN
        RAISERROR(N'Sequence code [%s] for Fiscal Year [%d] was not found.', 16, 1, @SequenceCode, @FiscalYear);
        RETURN;
    END;

    SELECT @GeneratedNo = ISNULL(Prefix, '') + RIGHT(REPLICATE('0', PaddingLength) + CAST(NewValue AS VARCHAR(20)), PaddingLength) + ISNULL(Suffix, '')
    FROM @NextVal;

    UPDATE sys.DocumentSequences 
    SET LastGeneratedNo = @GeneratedNo 
    WHERE TenantID = @TenantID AND CompanyID = @CompanyID AND SequenceCode = @SequenceCode AND FiscalYear = @FiscalYear;
END;
GO