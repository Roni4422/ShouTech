-- ============================================================================
-- 106 - Recurring sales schedules (SQLite)
--
-- Owner sales spec sections 3 + 5 (Sales_Cycle.txt), workstream W11: sales
-- orders and sales invoices recur from a TEMPLATE document. This migration
-- builds the generic template-based recurring engine (the mechanical basis
-- the L4 subscription layer will later sit on; no subscription-specific
-- shape is baked in here):
--   1. sre_recurring_schedules — one row per schedule: the template sales
--      document (sales_documents doc_id, kind SO or SAL — the generated
--      document always has the SAME kind as its template), the interval
--      (Daily / Weekly / Monthly / Yearly x count), the start / next-run /
--      end dates, an optional occurrence cap, the occurrences-created
--      counter, the lifecycle status (Active / Paused / Completed /
--      Cancelled), the auto-post flag and the last-run outcome, so a
--      failed generation or a failed auto-post is RECORDED on the
--      schedule and never silent.
--   2. sre_recurring_runs — the per-occurrence ledger. The UNIQUE
--      (schedule_id, occurrence_no) index is the idempotency marker of
--      the engine: an occurrence is CLAIMED (row inserted + schedule
--      advanced, one transaction) before its document is cloned, so
--      re-running generation for the same date can never create a second
--      document for the same occurrence.
--   3. sales_documents.recurring_schedule_id — the stamp every generated
--      document carries back to its schedule (a document_links row
--      template -> generated is also written by the generation service,
--      following the chain-link convention of migration 024).
--
-- Conventions mirror 097/100: company keying is the company CODE (TEXT) —
-- the party / master-data world; the template document itself is reached
-- through sales_documents, whose company key the service resolves.
-- Dates are ISO yyyy-MM-dd TEXT, like every other sales-cycle table.
--
-- Idempotent: CREATE TABLE / INDEX IF NOT EXISTS, the column add is
-- guarded by the migrator directive, permission seeds are
-- INSERT OR IGNORE + UPDATE; the checksum ledger skips a re-run.
-- ============================================================================

-- ---- Recurring schedules ----------------------------------------------------
CREATE TABLE IF NOT EXISTS sre_recurring_schedules (
    schedule_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    template_doc_id INTEGER NOT NULL,
    template_doc_kind TEXT NOT NULL CHECK (template_doc_kind IN ('SO','SAL')),
    template_doc_no TEXT NOT NULL DEFAULT '',
    interval_type TEXT NOT NULL CHECK (interval_type IN ('Daily','Weekly','Monthly','Yearly')),
    interval_count INTEGER NOT NULL CHECK (interval_count >= 1),
    start_date TEXT NOT NULL,
    next_run_date TEXT NOT NULL,
    end_date TEXT,
    max_occurrences INTEGER CHECK (max_occurrences IS NULL OR max_occurrences >= 1),
    occurrences_created INTEGER NOT NULL DEFAULT 0,
    auto_post INTEGER NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Active'
        CHECK (status IN ('Active','Paused','Completed','Cancelled')),
    last_run_date TEXT,
    last_run_status TEXT,
    last_generated_doc_id INTEGER,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT,
    created_by TEXT,
    updated_by TEXT
);
CREATE INDEX IF NOT EXISTS ix_sre_schedules_company_status
    ON sre_recurring_schedules(company_id, status) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_sre_schedules_due
    ON sre_recurring_schedules(status, next_run_date) WHERE is_deleted = 0;

-- ---- Per-occurrence run ledger (idempotency marker + audit) -----------------
CREATE TABLE IF NOT EXISTS sre_recurring_runs (
    run_id TEXT NOT NULL PRIMARY KEY,
    schedule_id TEXT NOT NULL,
    company_id TEXT NOT NULL,
    occurrence_no INTEGER NOT NULL,
    run_date TEXT NOT NULL,
    generated_doc_id INTEGER,
    outcome TEXT NOT NULL DEFAULT 'Pending'
        CHECK (outcome IN ('Pending','Draft','Posted','PostFailed','Failed')),
    error_text TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    completed_at TEXT,
    created_by TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_sre_runs_schedule_occurrence
    ON sre_recurring_runs(schedule_id, occurrence_no);
CREATE INDEX IF NOT EXISTS ix_sre_runs_schedule
    ON sre_recurring_runs(schedule_id, occurrence_no);

-- ---- Generated-document stamp -------------------------------------------------
-- @if-column-missing sales_documents.recurring_schedule_id
ALTER TABLE sales_documents ADD COLUMN recurring_schedule_id TEXT;
CREATE INDEX IF NOT EXISTS ix_sales_documents_recurring_schedule
    ON sales_documents(recurring_schedule_id) WHERE recurring_schedule_id IS NOT NULL;

-- ---- Permissions: SALES.RECURRING.VIEW / MANAGE (display_order after 400) ----
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.RECURRING.VIEW','Sales Recurring - View','SALES','RECURRING','VIEW','المبيعات المتكررة - عرض','Sales Recurring - View','View recurring sales schedules and their generation history (sales spec sections 3 and 5, migration 106)',401,0);
UPDATE permissions SET group_code='SALES',feature_code='RECURRING',operation_code='VIEW',name_ar='المبيعات المتكررة - عرض',name_en='Sales Recurring - View',display_order=401,is_sensitive=0 WHERE permission_code='SALES.RECURRING.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.RECURRING.MANAGE','Sales Recurring - Manage','SALES','RECURRING','MANAGE','المبيعات المتكررة - إدارة','Sales Recurring - Manage','Create, pause, resume, cancel and run recurring sales schedules (sales spec sections 3 and 5, migration 106)',402,1);
UPDATE permissions SET group_code='SALES',feature_code='RECURRING',operation_code='MANAGE',name_ar='المبيعات المتكررة - إدارة',name_en='Sales Recurring - Manage',display_order=402,is_sensitive=1 WHERE permission_code='SALES.RECURRING.MANAGE';

INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('SALES.RECURRING.VIEW','SALES.RECURRING.MANAGE');
