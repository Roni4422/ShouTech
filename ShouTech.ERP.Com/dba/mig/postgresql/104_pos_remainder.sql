-- ============================================================================
-- 104 - POS remainder (PostgreSQL)
--
-- PostgreSQL twin of dba/mig/sqlite/104_pos_remainder.sql — see that
-- file for the full design notes (session paid-outs subtracted from
-- expected cash, numeric per-user/per-role cashier discount limits,
-- the two-person discount-override audit, and the exchange_ref link
-- between an exchange's SRT return and POS sale). Dialect differences
-- only: NUMERIC money/percentages, BOOLEAN flags, TIMESTAMPTZ audit
-- stamps, ADD COLUMN IF NOT EXISTS, ON CONFLICT seed upserts.
-- ============================================================================

-- ---- Session paid-outs -------------------------------------------------------
CREATE TABLE IF NOT EXISTS pos_session_expenses (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    session_id TEXT NOT NULL REFERENCES pos_sessions(id),
    expense_no TEXT NOT NULL,
    amount NUMERIC NOT NULL DEFAULT 0,
    reason TEXT NOT NULL DEFAULT '',
    reference_no TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_pos_session_expenses_company_no
    ON pos_session_expenses(company_id, expense_no) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_pos_session_expenses_session
    ON pos_session_expenses(company_id, session_id) WHERE is_deleted = FALSE;

-- ---- Numeric cashier discount limits ------------------------------------------
CREATE TABLE IF NOT EXISTS pos_discount_limits (
    company_id TEXT NOT NULL,
    subject_kind TEXT NOT NULL CHECK (subject_kind IN ('USER','ROLE')),
    subject_key TEXT NOT NULL,
    max_discount_pct NUMERIC NOT NULL DEFAULT 0,
    updated_by TEXT,
    updated_at TIMESTAMPTZ,
    PRIMARY KEY (company_id, subject_kind, subject_key)
);

-- ---- Discount override audit ----------------------------------------------------
CREATE TABLE IF NOT EXISTS pos_discount_overrides (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    doc_kind TEXT,
    doc_no TEXT,
    doc_id INTEGER,
    cashier TEXT NOT NULL,
    approved_by TEXT NOT NULL,
    limit_pct NUMERIC NOT NULL DEFAULT 0,
    effective_pct NUMERIC NOT NULL DEFAULT 0,
    max_line_pct NUMERIC NOT NULL DEFAULT 0,
    discount_amount NUMERIC NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_pos_discount_overrides_doc
    ON pos_discount_overrides(company_id, doc_kind, doc_no);

-- ---- Exchange link ----------------------------------------------------------------
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS exchange_ref TEXT;
CREATE INDEX IF NOT EXISTS ix_sales_documents_exchange_ref
    ON sales_documents(company_id, exchange_ref) WHERE is_deleted = FALSE;

-- ---- Permission: POS.EXPENSE.MANAGE -------------------------------------------------
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('POS.EXPENSE.MANAGE','POS Expense - Manage','POS','EXPENSE','MANAGE','مصروفات نقاط البيع - إدارة','POS Expense - Manage','Record cash paid out of the drawer during an open POS session (sales cycle W9, migration 104)',401,true) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('POS.EXPENSE.MANAGE') ON CONFLICT DO NOTHING;
