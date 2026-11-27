-- ============================================================================
-- 106 - Recurring sales schedules (PostgreSQL)
--
-- PostgreSQL twin of dba/mig/sqlite/106_sre_recurring_schedules.sql — see
-- that file for the full design notes (template-based recurring sales
-- orders / invoices, the per-occurrence run ledger whose UNIQUE
-- (schedule_id, occurrence_no) index is the generation idempotency
-- marker, and the sales_documents.recurring_schedule_id stamp).
-- Dialect differences only: BOOLEAN flags, TIMESTAMPTZ audit stamps,
-- ADD COLUMN IF NOT EXISTS, and ON CONFLICT seed upserts.
-- ============================================================================

-- ---- Recurring schedules ----------------------------------------------------
CREATE TABLE IF NOT EXISTS sre_recurring_schedules (
    schedule_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    template_doc_id BIGINT NOT NULL,
    template_doc_kind TEXT NOT NULL CHECK (template_doc_kind IN ('SO','SAL')),
    template_doc_no TEXT NOT NULL DEFAULT '',
    interval_type TEXT NOT NULL CHECK (interval_type IN ('Daily','Weekly','Monthly','Yearly')),
    interval_count INTEGER NOT NULL CHECK (interval_count >= 1),
    start_date TEXT NOT NULL,
    next_run_date TEXT NOT NULL,
    end_date TEXT,
    max_occurrences INTEGER CHECK (max_occurrences IS NULL OR max_occurrences >= 1),
    occurrences_created INTEGER NOT NULL DEFAULT 0,
    auto_post BOOLEAN NOT NULL DEFAULT FALSE,
    status TEXT NOT NULL DEFAULT 'Active'
        CHECK (status IN ('Active','Paused','Completed','Cancelled')),
    last_run_date TEXT,
    last_run_status TEXT,
    last_generated_doc_id BIGINT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT
);
CREATE INDEX IF NOT EXISTS ix_sre_schedules_company_status
    ON sre_recurring_schedules(company_id, status) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_sre_schedules_due
    ON sre_recurring_schedules(status, next_run_date) WHERE is_deleted = FALSE;

-- ---- Per-occurrence run ledger (idempotency marker + audit) -----------------
CREATE TABLE IF NOT EXISTS sre_recurring_runs (
    run_id TEXT NOT NULL PRIMARY KEY,
    schedule_id TEXT NOT NULL,
    company_id TEXT NOT NULL,
    occurrence_no INTEGER NOT NULL,
    run_date TEXT NOT NULL,
    generated_doc_id BIGINT,
    outcome TEXT NOT NULL DEFAULT 'Pending'
        CHECK (outcome IN ('Pending','Draft','Posted','PostFailed','Failed')),
    error_text TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    completed_at TEXT,
    created_by TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_sre_runs_schedule_occurrence
    ON sre_recurring_runs(schedule_id, occurrence_no);
CREATE INDEX IF NOT EXISTS ix_sre_runs_schedule
    ON sre_recurring_runs(schedule_id, occurrence_no);

-- ---- Generated-document stamp -------------------------------------------------
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS recurring_schedule_id TEXT;
CREATE INDEX IF NOT EXISTS ix_sales_documents_recurring_schedule
    ON sales_documents(recurring_schedule_id) WHERE recurring_schedule_id IS NOT NULL;

-- ---- Permissions: SALES.RECURRING.VIEW / MANAGE (display_order after 400) ----
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.RECURRING.VIEW','Sales Recurring - View','SALES','RECURRING','VIEW','المبيعات المتكررة - عرض','Sales Recurring - View','View recurring sales schedules and their generation history (sales spec sections 3 and 5, migration 106)',401,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.RECURRING.MANAGE','Sales Recurring - Manage','SALES','RECURRING','MANAGE','المبيعات المتكررة - إدارة','Sales Recurring - Manage','Create, pause, resume, cancel and run recurring sales schedules (sales spec sections 3 and 5, migration 106)',402,true) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('SALES.RECURRING.VIEW','SALES.RECURRING.MANAGE') ON CONFLICT DO NOTHING;
