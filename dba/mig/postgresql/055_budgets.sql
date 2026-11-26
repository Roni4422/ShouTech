-- ============================================================================
-- 055 - Budgets (PostgreSQL)
-- PostgreSQL counterpart of sqlite/055_budgets.sql (Phase B design
-- section 3). See the SQLite file for the design notes, the BG1-BG3
-- decisions, the fiscal_year_id type adaptation (BIGINT = the real
-- fiscal_years primary key on this provider) and the numbering note
-- (055 applies cleanly after 059-064).
-- ============================================================================

-- ---- Budget headers ---------------------------------------------------------
CREATE TABLE IF NOT EXISTS budgets (
    budget_id uuid NOT NULL PRIMARY KEY,
    company_id uuid NOT NULL,
    fiscal_year_id BIGINT NOT NULL,
    name TEXT NOT NULL,
    budget_against TEXT NOT NULL DEFAULT 'CostCenter'
        CHECK (budget_against IN ('CostCenter','Account','Project')),
    status TEXT NOT NULL DEFAULT 'Draft'
        CHECK (status IN ('Draft','Active','Closed')),
    action_annual TEXT NOT NULL DEFAULT 'Stop'
        CHECK (action_annual IN ('Stop','Warn','Ignore')),
    action_monthly TEXT NOT NULL DEFAULT 'Warn'
        CHECK (action_monthly IN ('Stop','Warn','Ignore')),
    notes TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT
);
CREATE INDEX IF NOT EXISTS ix_budgets_company_year_status
    ON budgets(company_id, fiscal_year_id, status)
    WHERE is_deleted = FALSE;

-- ---- Budget lines -------------------------------------------------------------
CREATE TABLE IF NOT EXISTS budget_lines (
    budget_line_id uuid NOT NULL PRIMARY KEY,
    budget_id uuid NOT NULL,
    account_id uuid NOT NULL,
    account_code TEXT NOT NULL,
    cost_center_id uuid,
    project_id uuid,
    annual_amount NUMERIC NOT NULL DEFAULT 0 CHECK (annual_amount >= 0),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ
);
CREATE INDEX IF NOT EXISTS ix_budget_lines_budget
    ON budget_lines(budget_id);
CREATE INDEX IF NOT EXISTS ix_budget_lines_account
    ON budget_lines(account_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_budget_lines_dimension
    ON budget_lines(budget_id, account_id,
                    COALESCE(cost_center_id, '00000000-0000-0000-0000-000000000000'::uuid),
                    COALESCE(project_id, '00000000-0000-0000-0000-000000000000'::uuid));

-- ---- Monthly distribution -------------------------------------------------------
CREATE TABLE IF NOT EXISTS budget_period_amounts (
    budget_line_id uuid NOT NULL,
    period_no INTEGER NOT NULL CHECK (period_no BETWEEN 1 AND 12),
    amount NUMERIC NOT NULL DEFAULT 0 CHECK (amount >= 0),
    PRIMARY KEY (budget_line_id, period_no)
);

-- ---- Control events (audit of every Warn / Block) --------------------------------
CREATE TABLE IF NOT EXISTS budget_control_events (
    event_id uuid NOT NULL PRIMARY KEY,
    company_id uuid NOT NULL,
    budget_id uuid NOT NULL,
    budget_line_id uuid NOT NULL,
    journal_entry_id uuid,
    action_taken TEXT NOT NULL CHECK (action_taken IN ('Warned','Blocked','Overridden')),
    horizon TEXT NOT NULL CHECK (horizon IN ('Annual','Monthly')),
    budget_amount NUMERIC NOT NULL DEFAULT 0,
    actual_amount NUMERIC NOT NULL DEFAULT 0,
    overage_amount NUMERIC NOT NULL DEFAULT 0,
    actor TEXT,
    reason TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_budget_control_events_budget
    ON budget_control_events(company_id, budget_id, created_at);
CREATE INDEX IF NOT EXISTS ix_budget_control_events_entry
    ON budget_control_events(journal_entry_id);

-- ---- Permissions (028/038/053/054 pattern) ----------------------------------------
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.BUDGET.VIEW','Accounting Budget - View','ACCOUNTING','BUDGET','VIEW','Accounting Budget - View','Accounting Budget - View','View budgets and budget-vs-actual variance (Phase B 055)',346,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.BUDGET.EDIT','Accounting Budget - Edit','ACCOUNTING','BUDGET','EDIT','Accounting Budget - Edit','Accounting Budget - Edit','Create and edit budget definitions, lines and monthly distributions (Phase B 055)',347,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.BUDGET.ACTIVATE','Accounting Budget - Activate','ACCOUNTING','BUDGET','ACTIVATE','Accounting Budget - Activate','Accounting Budget - Activate','Activate or close a budget so the posting guard enforces it (Phase B 055)',348,true) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('ACC.BUDGET.VIEW','ACC.BUDGET.EDIT','ACC.BUDGET.ACTIVATE') ON CONFLICT DO NOTHING;
INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('ACC.BUDGET.VIEW','ACC.BUDGET.EDIT') ON CONFLICT DO NOTHING;
INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_VIEWER' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('ACC.BUDGET.VIEW') ON CONFLICT DO NOTHING;
