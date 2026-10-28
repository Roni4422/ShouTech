-- ========================================================================
-- FILE: dba/tmpl/tmpl.sql
-- PROJECT: SHOUTECH ERP V10 - Ultimate Enterprise Edition
-- PURPOSE: Master Database Orchestrator for Fiscal Year Schema Deployment
-- VERSION: 10.2.0 (New folder structure)
-- DEPENDENCIES: All SQL files organized in subfolders (mst, fin, crm, inv, hr, mfg, pos, ai, aud, sys, post, sed)
-- ========================================================================
-- USAGE: sqlcmd -S . -d master -v FiscalYearDb="SHOUTECH_2025" CompanyID="..." FiscalYear="2025" -i tmpl.sql
-- ========================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

DECLARE @DeploymentID UNIQUEIDENTIFIER = NEWID();
DECLARE @StartTime DATETIME2(7) = SYSUTCDATETIME();
DECLARE @FiscalYearDb SYSNAME = '$(FiscalYearDb)';
DECLARE @CompanyID UNIQUEIDENTIFIER = '$(CompanyID)';
DECLARE @FiscalYear INT = '$(FiscalYear)';
DECLARE @ScriptVersion NVARCHAR(20) = '10.2.0';

PRINT '═══════════════════════════════════════════════════════════════════════════';
PRINT '🚀 SHOUTECH ERP V10 - Ultimate Deployment Started';
PRINT '   Deployment ID : ' + CONVERT(NVARCHAR(36), @DeploymentID);
PRINT '   Database      : ' + @FiscalYearDb;
PRINT '   Fiscal Year   : ' + CAST(@FiscalYear AS NVARCHAR(4));
PRINT '   Start Time    : ' + CONVERT(NVARCHAR(27), @StartTime, 121);
PRINT '═══════════════════════════════════════════════════════════════════════════';
GO

-- ==========================================================================
-- 1. Pre-Deployment Validation
-- ==========================================================================
IF DB_NAME() != '$(FiscalYearDb)'
BEGIN
    PRINT '❌ خطأ: يجب تنفيذ السكريبت داخل قاعدة السنة المالية';
    RAISERROR('Invalid database context', 16, 1);
    RETURN;
END

IF '$(FiscalYearDb)' IS NULL OR '$(FiscalYearDb)' = ''
BEGIN
    RAISERROR('FiscalYearDb parameter is required', 16, 1);
    RETURN;
END
GO

-- ==========================================================================
-- 2. Create SchemaVersion Table (if not exists)
-- ==========================================================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SchemaVersion' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.SchemaVersion (
        DeploymentID UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        ScriptName NVARCHAR(255) NOT NULL,
        Version NVARCHAR(50) NOT NULL,
        AppliedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
        AppliedBy NVARCHAR(128) NOT NULL DEFAULT SYSTEM_USER,
        DurationMS INT,
        Success BIT NOT NULL DEFAULT 1,
        ErrorMessage NVARCHAR(MAX),
        CompanyID UNIQUEIDENTIFIER,
        FiscalYear INT
    );

    CREATE INDEX IX_SchemaVersion_AppliedAt ON dbo.SchemaVersion(AppliedAt DESC);
    PRINT '✅ SchemaVersion table created';
END
GO

-- ==========================================================================
-- 3. Execute Schemas in Correct Dependency Order
-- ==========================================================================
BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @Step INT = 0;
    DECLARE @StepStart DATETIME2(7);

    -- ======================================================================
    -- MASTER DATABASE (MST) – must be first
    -- ======================================================================
    SET @Step = 1; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → MST Core (mst.sql)';
    :r "../mst/mst.sql"
    INSERT INTO dbo.SchemaVersion (ScriptName, Version, DurationMS, CompanyID, FiscalYear)
    VALUES ('mst.sql', @ScriptVersion, DATEDIFF(MS, @StepStart, SYSUTCDATETIME()), @CompanyID, @FiscalYear);

    SET @Step = 2; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → MST Licensing (lic.sql)';
    :r "../mst/lic.sql"
    INSERT INTO dbo.SchemaVersion (ScriptName, Version, DurationMS, CompanyID, FiscalYear)
    VALUES ('lic.sql', @ScriptVersion, DATEDIFF(MS, @StepStart, SYSUTCDATETIME()), @CompanyID, @FiscalYear);

    SET @Step = 3; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → MST Users & Permissions (usr.sql)';
    :r "../mst/usr.sql"
    INSERT INTO dbo.SchemaVersion (ScriptName, Version, DurationMS, CompanyID, FiscalYear)
    VALUES ('usr.sql', @ScriptVersion, DATEDIFF(MS, @StepStart, SYSUTCDATETIME()), @CompanyID, @FiscalYear);

    SET @Step = 4; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → MST Audit (mst_audit.sql)';
    :r "../mst/mst_audit.sql"
    INSERT INTO dbo.SchemaVersion (ScriptName, Version, DurationMS, CompanyID, FiscalYear)
    VALUES ('mst_audit.sql', @ScriptVersion, DATEDIFF(MS, @StepStart, SYSUTCDATETIME()), @CompanyID, @FiscalYear);

    SET @Step = 5; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → MST Stored Procedures (proc.sql)';
    :r "../mst/proc.sql"
    INSERT INTO dbo.SchemaVersion (ScriptName, Version, DurationMS, CompanyID, FiscalYear)
    VALUES ('proc.sql', @ScriptVersion, DATEDIFF(MS, @StepStart, SYSUTCDATETIME()), @CompanyID, @FiscalYear);

    -- ======================================================================
    -- FINANCE MODULE (depends only on MST)
    -- ======================================================================
    SET @Step = 6; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → Finance: Chart of Accounts (fin_coa.sql)';
    :r "../fin/fin_coa.sql"
    INSERT INTO dbo.SchemaVersion (ScriptName, Version, DurationMS, CompanyID, FiscalYear)
    VALUES ('fin_coa.sql', @ScriptVersion, DATEDIFF(MS, @StepStart, SYSUTCDATETIME()), @CompanyID, @FiscalYear);

    SET @Step = 7; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → Finance: GL (fin_gl.sql)';
    :r "../fin/fin_gl.sql"
    INSERT INTO dbo.SchemaVersion (ScriptName, Version, DurationMS, CompanyID, FiscalYear)
    VALUES ('fin_gl.sql', @ScriptVersion, DATEDIFF(MS, @StepStart, SYSUTCDATETIME()), @CompanyID, @FiscalYear);

    SET @Step = 8; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → Finance: Invoices (fin_invoices.sql)';
    :r "../fin/fin_invoices.sql"

    SET @Step = 9; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → Finance: Payments (fin_payments.sql)';
    :r "../fin/fin_payments.sql"

    SET @Step = 10; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → Finance: Fixed Assets (fin_assets.sql)';
    :r "../fin/fin_assets.sql"

    SET @Step = 11; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → Finance: Reports (fin_rpt.sql)';
    :r "../fin/fin_rpt.sql"

    -- ======================================================================
    -- CRM MODULE (Customers, Suppliers – required by Inventory)
    -- ======================================================================
    SET @Step = 12; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → CRM: Customers & Groups (crm_cust.sql)';
    :r "../crm/crm_cust.sql"

    SET @Step = 13; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → CRM: Leads (crm_lead.sql)';
    :r "../crm/crm_lead.sql"

    SET @Step = 14; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → CRM: Contracts (crm_cont.sql)';
    :r "../crm/crm_cont.sql"

    SET @Step = 15; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → CRM: Price Lists (crm_pricelists.sql)';
    :r "../crm/crm_pricelists.sql"

    -- ======================================================================
    -- INVENTORY MODULE (depends on CRM for Suppliers)
    -- ======================================================================
    SET @Step = 16; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → Inventory: Products (inv_prod.sql)';
    :r "../inv/inv_prod.sql"

    SET @Step = 17; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → Inventory: Batches (inv_batches.sql)';
    :r "../inv/inv_batches.sql"

    SET @Step = 18; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → Inventory: Warehouses (inv_wh.sql)';
    :r "../inv/inv_wh.sql"

    SET @Step = 19; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → Inventory: Stock Balances (inv_stock.sql)';
    :r "../inv/inv_stock.sql"

    SET @Step = 20; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → Inventory: Movements (inv_move.sql)';
    :r "../inv/inv_move.sql"

    SET @Step = 21; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → Inventory: Valuation (inv_val.sql)';
    :r "../inv/inv_val.sql"

    -- ======================================================================
    -- HUMAN RESOURCES MODULE (independent)
    -- ======================================================================
    SET @Step = 22; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → HR: Employees (hr_emp.sql)';
    :r "../hr/hr_emp.sql"

    SET @Step = 23; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → HR: Payroll (hr_payr.sql)';
    :r "../hr/hr_payr.sql"

    SET @Step = 24; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → HR: Attendance (hr_att.sql)';
    :r "../hr/hr_att.sql"

    SET @Step = 25; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → HR: Leaves (hr_leave.sql)';
    :r "../hr/hr_leave.sql"

    -- ======================================================================
    -- MANUFACTURING MODULE (depends on Inventory)
    -- ======================================================================
    SET @Step = 26; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → MFG: BOM (mfg_bom.sql)';
    :r "../mfg/mfg_bom.sql"

    SET @Step = 27; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → MFG: Work Orders (mfg_wo.sql)';
    :r "../mfg/mfg_wo.sql"

    SET @Step = 28; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → MFG: Routing (mfg_route.sql)';
    :r "../mfg/mfg_route.sql"

    SET @Step = 29; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → MFG: Quality (mfg_qual.sql)';
    :r "../mfg/mfg_qual.sql"

    -- ======================================================================
    -- POINT OF SALE MODULE
    -- ======================================================================
    SET @Step = 30; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → POS: Sessions & Transactions (pos.sql)';
    :r "../pos/pos.sql"

    SET @Step = 31; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → POS: Shifts (pos_shift.sql)';
    :r "../pos/pos_shift.sql"

    SET @Step = 32; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → POS: Payments (pos_transactions.sql)';
    :r "../pos/pos_transactions.sql"

    -- ======================================================================
    -- AI MODULE (independent)
    -- ======================================================================
    SET @Step = 33; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → AI: Models (ai_model.sql)';
    :r "../ai/ai_model.sql"

    SET @Step = 34; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → AI: Predictions (ai_pred.sql)';
    :r "../ai/ai_pred.sql"

    SET @Step = 35; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → AI: Anomalies (ai_anom.sql)';
    :r "../ai/ai_anom.sql"

    -- ======================================================================
    -- SYSTEM MODULE (depends on Inventory and CRM)
    -- ======================================================================
    SET @Step = 36; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → System: Settings (sys_sett.sql)';
    :r "../sys/sys_sett.sql"

    SET @Step = 37; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → System: Sequences (sys_seq.sql)';
    :r "../sys/sys_seq.sql"

    SET @Step = 38; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → System: Tax Config (sys_tax.sql)';
    :r "../sys/sys_tax.sql"

    SET @Step = 39; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → System: Notifications (sys_noti.sql)';
    :r "../sys/sys_noti.sql"
	:r "../aud/log.sql"
	:r "../aud/appr.sql"
	:r "../aud/retn.sql"

	-- AI module
	:r "../ai/model.sql"
	:r "../ai/pred.sql"
	:r "../ai/anom.sql"
    -- ======================================================================
    -- AUDITING MODULE (depends on all previous tables)
    -- ======================================================================
    SET @Step = 40; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → Audit: Logs (aud_log.sql)';
    :r "../aud/aud_log.sql"

    SET @Step = 41; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → Audit: Approvals (aud_appr.sql)';
    :r "../aud/aud_appr.sql"

    SET @Step = 42; SET @StepStart = SYSUTCDATETIME();
    PRINT '📋 [' + CAST(@Step AS VARCHAR) + '/14] → Audit: Retention Policy (aud_retn.sql)';
    :r "../aud/aud_retn.sql"

    COMMIT TRANSACTION;
    PRINT '✅ All base schemas deployed successfully.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;

    DECLARE @ErrorMsg NVARCHAR(MAX) = ERROR_MESSAGE();
    DECLARE @ErrorLine INT = ERROR_LINE();
    DECLARE @ErrorNumber INT = ERROR_NUMBER();

    PRINT '❌ Deployment failed at step ' + CAST(@Step AS VARCHAR) + ' - Line: ' + CAST(@ErrorLine AS NVARCHAR);
    PRINT '   Error: ' + @ErrorMsg;

    INSERT INTO dbo.SchemaVersion (ScriptName, Version, Success, ErrorMessage, CompanyID, FiscalYear)
    VALUES ('tmpl.sql - Step ' + CAST(@Step AS NVARCHAR), @ScriptVersion, 0,
            @ErrorMsg + ' (Line ' + CAST(@ErrorLine AS NVARCHAR) + ')', @CompanyID, @FiscalYear);

    RAISERROR('Deployment failed at step %d: %s', 16, 1, @Step, @ErrorMsg);
    RETURN;
END CATCH
GO

-- ==========================================================================
-- 4. Post-Deployment (Foreign Keys, RLS, Indexes)
-- ==========================================================================
PRINT '📋 Post-Deployment → post.sql';
:r "../post/post.sql"
GO

-- ==========================================================================
-- 5. Seed Data (optional – can be commented out)
-- ==========================================================================
PRINT '🌱 Seeding demo data...';
:r "../sed/sds_coa.sql"
:r "../sed/sds_crm.sql"
:r "../sed/sds_inv.sql"
:r "../sed/sds_fin.sql"
:r "../sed/sds_hr.sql"
:r "../sed/sds_sys.sql"
PRINT '✅ Demo data seeded.';
GO

-- ==========================================================================
-- 6. Final Success Logging
-- ==========================================================================
INSERT INTO dbo.SchemaVersion (ScriptName, Version, DurationMS, Success, CompanyID, FiscalYear)
VALUES ('tmpl.sql', @ScriptVersion, DATEDIFF(MS, @StartTime, SYSUTCDATETIME()), 1, @CompanyID, @FiscalYear);

PRINT '═══════════════════════════════════════════════════════════════════════════';
PRINT '✅ Fiscal year database deployed successfully (Ultimate Edition)';
PRINT '   Deployment ID : ' + CONVERT(NVARCHAR(36), @DeploymentID);
PRINT '   Duration      : ' + CAST(DATEDIFF(SECOND, @StartTime, SYSUTCDATETIME()) AS NVARCHAR) + ' seconds';
PRINT '   Status        : SUCCESS';
PRINT '═══════════════════════════════════════════════════════════════════════════';
GO