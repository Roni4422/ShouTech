-- ============================================================================
-- 055 - Budgets (SQLite)
--
-- Phase B design section 3 (migration 055): budgets live in the JOURNAL
-- world (Guid company id, journal_entry_lines, journal_line_cost_splits),
-- because that is where actuals and the posting guard belong.
--
--   * budgets: one budget per (company, fiscal year): what it is against
--     (CostCenter / Account / Project, owner decision BG3 includes
--     Project), its lifecycle status (Draft / Active / Closed - only
--     Active budgets are enforced, the ERPNext rule), and the control
--     action per horizon (action_annual / action_monthly, each
--     Stop / Warn / Ignore - the ERPNext control matrix). These
--     per-budget actions ARE the enforcement mode; the design specifies
--     no separate company-level mode switch.
--   * budget_lines: one row per (account x cost center x project) with
--     the annual amount. account_code is a snapshot for reporting.
--     cost_center_id references cost_centers.id (031); project_id
--     references projects.project_id (026); account_id references the
--     canonical chart_of_accounts.account_id (009/059).
--   * budget_period_amounts: the monthly distribution of a line
--     (period_no 1-12 within the budget's fiscal year). NO rows for a
--     line means the annual amount is split equally across the year;
--     when rows exist they are authoritative (the service layer keeps
--     their sum equal to the line's annual amount).
--   * budget_control_events: every Warn and Block the BudgetGuard
--     produces (design: every warn, block and override is auditable;
--     owner decision BG1 = no override in v1, so no Overridden rows
--     exist yet - the action_taken vocabulary reserves the value).
--
-- GROUND-TRUTH ADAPTATIONS vs the design text (documented in
-- docs/BUILD_PHASEB_055.md):
--   * fiscal_year_id is INTEGER here (BIGINT on PostgreSQL): that is
--     the real primary-key type of fiscal_years on both providers
--     (001/002), not TEXT as the design sketch wrote.
--   * The accounts.budget opt-in flag lives on the CANONICAL chart
--     (chart_of_accounts.budget, migration 059) since step 2 dropped
--     the legacy accounts table. BudgetGuard enforces only lines whose
--     account carries the flag; BudgetService sets the flag
--     transactionally when an account gains a budget line, so the flag
--     is the designed opt-in marker and never a silent trap.
--
-- NUMBERING NOTE: like 053/054, this file sorts before the
-- already-shipped 059-064 files; the migrator applies pending
-- migrations in filename order and skips by checksum. 055 depends on
-- 001/009 (fiscal years, chart), 026 (projects), 031 (cost centers),
-- 052 (cost splits) and 002-004 (roles/permissions) objects, never on
-- 059-064-only objects (chart_of_accounts.budget arrives with 059; the
-- guard reads it defensively so a pre-059 database simply enforces
-- nothing until unified).
--
-- Idempotent: table/index creation is IF NOT EXISTS, permission seeds
-- are INSERT OR IGNORE + UPDATE, and the checksum ledger skips a
-- re-run.
-- ============================================================================

-- ---- Budget headers ---------------------------------------------------------
CREATE TABLE IF NOT EXISTS budgets (
    budget_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    fiscal_year_id INTEGER NOT NULL,
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
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT,
    created_by TEXT,
    updated_by TEXT
);
CREATE INDEX IF NOT EXISTS ix_budgets_company_year_status
    ON budgets(company_id, fiscal_year_id, status)
    WHERE is_deleted = 0;

-- ---- Budget lines -------------------------------------------------------------
CREATE TABLE IF NOT EXISTS budget_lines (
    budget_line_id TEXT NOT NULL PRIMARY KEY,
    budget_id TEXT NOT NULL,
    account_id TEXT NOT NULL,
    account_code TEXT NOT NULL,
    cost_center_id TEXT,
    project_id TEXT,
    annual_amount NUMERIC NOT NULL DEFAULT 0 CHECK (annual_amount >= 0),
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT
);
CREATE INDEX IF NOT EXISTS ix_budget_lines_budget
    ON budget_lines(budget_id);
CREATE INDEX IF NOT EXISTS ix_budget_lines_account
    ON budget_lines(account_id);
-- One line per (budget, account, dimension): NULL dimensions are folded
-- to '' so the unique index actually binds (plain UNIQUE treats NULLs
-- as distinct on both providers).
CREATE UNIQUE INDEX IF NOT EXISTS ux_budget_lines_dimension
    ON budget_lines(budget_id, account_id,
                    COALESCE(cost_center_id, ''), COALESCE(project_id, ''));

-- ---- Monthly distribution -------------------------------------------------------
CREATE TABLE IF NOT EXISTS budget_period_amounts (
    budget_line_id TEXT NOT NULL,
    period_no INTEGER NOT NULL CHECK (period_no BETWEEN 1 AND 12),
    amount NUMERIC NOT NULL DEFAULT 0 CHECK (amount >= 0),
    PRIMARY KEY (budget_line_id, period_no)
);

-- ---- Control events (audit of every Warn / Block) --------------------------------
CREATE TABLE IF NOT EXISTS budget_control_events (
    event_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    budget_id TEXT NOT NULL,
    budget_line_id TEXT NOT NULL,
    journal_entry_id TEXT,
    action_taken TEXT NOT NULL CHECK (action_taken IN ('Warned','Blocked','Overridden')),
    horizon TEXT NOT NULL CHECK (horizon IN ('Annual','Monthly')),
    budget_amount NUMERIC NOT NULL DEFAULT 0,
    actual_amount NUMERIC NOT NULL DEFAULT 0,
    overage_amount NUMERIC NOT NULL DEFAULT 0,
    actor TEXT,
    reason TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_budget_control_events_budget
    ON budget_control_events(company_id, budget_id, created_at);
CREATE INDEX IF NOT EXISTS ix_budget_control_events_entry
    ON budget_control_events(journal_entry_id);

-- ---- Permissions (028/038/053/054 pattern) ----------------------------------------
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.BUDGET.VIEW','Accounting Budget - View','ACCOUNTING','BUDGET','VIEW','Accounting Budget - View','Accounting Budget - View','View budgets and budget-vs-actual variance (Phase B 055)',346,0);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='BUDGET',operation_code='VIEW',name_ar='Accounting Budget - View',name_en='Accounting Budget - View',display_order=346,is_sensitive=0 WHERE permission_code='ACC.BUDGET.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.BUDGET.EDIT','Accounting Budget - Edit','ACCOUNTING','BUDGET','EDIT','Accounting Budget - Edit','Accounting Budget - Edit','Create and edit budget definitions, lines and monthly distributions (Phase B 055)',347,0);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='BUDGET',operation_code='EDIT',name_ar='Accounting Budget - Edit',name_en='Accounting Budget - Edit',display_order=347,is_sensitive=0 WHERE permission_code='ACC.BUDGET.EDIT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.BUDGET.ACTIVATE','Accounting Budget - Activate','ACCOUNTING','BUDGET','ACTIVATE','Accounting Budget - Activate','Accounting Budget - Activate','Activate or close a budget so the posting guard enforces it (Phase B 055)',348,1);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='BUDGET',operation_code='ACTIVATE',name_ar='Accounting Budget - Activate',name_en='Accounting Budget - Activate',display_order=348,is_sensitive=1 WHERE permission_code='ACC.BUDGET.ACTIVATE';

INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('ACC.BUDGET.VIEW','ACC.BUDGET.EDIT','ACC.BUDGET.ACTIVATE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('ACC.BUDGET.VIEW','ACC.BUDGET.EDIT');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_VIEWER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('ACC.BUDGET.VIEW');
