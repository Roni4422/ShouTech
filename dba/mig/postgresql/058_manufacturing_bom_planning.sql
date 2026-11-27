-- ============================================================================
-- 058 - Manufacturing build-out: BOM master, staged workflow, WIP/variance
--       GL support and MRP-lite planning (PostgreSQL)
-- PostgreSQL counterpart of sqlite/058_manufacturing_bom_planning.sql
-- (Build/manufacturing, docs/BUILD_MANUFACTURING.md). See the SQLite file
-- for the design notes, the GL account codes (WIP 1211, Finished Goods
-- 1212, Variance 406, Applied Overhead 409) and the numbering note.
-- Differences: uuid ids, NUMERIC money/quantities, BOOLEAN flags,
-- TIMESTAMPTZ audit stamps, ADD COLUMN IF NOT EXISTS, and the status
-- CHECK is widened in place through a DO block (no table rebuild).
-- ============================================================================

-- ---- BOM master ---------------------------------------------------------------
CREATE TABLE IF NOT EXISTS mfg_boms (
    id uuid PRIMARY KEY,
    company_id TEXT NOT NULL,
    material_code TEXT NOT NULL,
    material_name TEXT,
    version INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    is_default BOOLEAN NOT NULL DEFAULT FALSE,
    output_qty NUMERIC NOT NULL DEFAULT 1 CHECK (output_qty > 0),
    output_unit TEXT NOT NULL DEFAULT 'PCS',
    notes TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_mfg_boms_company_material_version
    ON mfg_boms(company_id, material_code, version) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_mfg_boms_company_material
    ON mfg_boms(company_id, material_code, is_active, is_default) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS mfg_bom_lines (
    id uuid PRIMARY KEY,
    bom_id uuid NOT NULL REFERENCES mfg_boms(id),
    line_no INTEGER NOT NULL DEFAULT 0,
    component_code TEXT NOT NULL,
    component_name TEXT,
    qty NUMERIC NOT NULL CHECK (qty > 0),
    unit_code TEXT,
    scrap_pct NUMERIC NOT NULL DEFAULT 0 CHECK (scrap_pct >= 0 AND scrap_pct < 100),
    warehouse_code TEXT,
    standard_unit_cost NUMERIC NOT NULL DEFAULT 0 CHECK (standard_unit_cost >= 0)
);
CREATE INDEX IF NOT EXISTS ix_mfg_bom_lines_bom
    ON mfg_bom_lines(bom_id, line_no);

-- ---- Staged workflow columns on mfg_orders -------------------------------------
ALTER TABLE mfg_orders ADD COLUMN IF NOT EXISTS bom_id uuid;
ALTER TABLE mfg_orders ADD COLUMN IF NOT EXISTS bom_version INTEGER;
ALTER TABLE mfg_orders ADD COLUMN IF NOT EXISTS planned_start_date TEXT;
ALTER TABLE mfg_orders ADD COLUMN IF NOT EXISTS planned_end_date TEXT;
ALTER TABLE mfg_orders ADD COLUMN IF NOT EXISTS sales_order_doc_id INTEGER;
ALTER TABLE mfg_orders ADD COLUMN IF NOT EXISTS sales_order_no TEXT;
ALTER TABLE mfg_orders ADD COLUMN IF NOT EXISTS cost_center TEXT;
ALTER TABLE mfg_orders ADD COLUMN IF NOT EXISTS source_warehouse_code TEXT;
ALTER TABLE mfg_orders ADD COLUMN IF NOT EXISTS wip_warehouse_code TEXT;
ALTER TABLE mfg_orders ADD COLUMN IF NOT EXISTS target_warehouse_code TEXT;
ALTER TABLE mfg_orders ADD COLUMN IF NOT EXISTS scrap_warehouse_code TEXT;
ALTER TABLE mfg_orders ADD COLUMN IF NOT EXISTS produced_qty NUMERIC NOT NULL DEFAULT 0;
ALTER TABLE mfg_orders ADD COLUMN IF NOT EXISTS accumulated_cost NUMERIC NOT NULL DEFAULT 0;
ALTER TABLE mfg_orders ADD COLUMN IF NOT EXISTS wip_balance NUMERIC NOT NULL DEFAULT 0;
ALTER TABLE mfg_orders ADD COLUMN IF NOT EXISTS variance_amount NUMERIC NOT NULL DEFAULT 0;
ALTER TABLE mfg_orders ADD COLUMN IF NOT EXISTS standard_variance NUMERIC NOT NULL DEFAULT 0;

DO $$
DECLARE cname TEXT;
BEGIN
    SELECT conname INTO cname FROM pg_constraint
    WHERE conrelid = 'mfg_orders'::regclass AND contype = 'c'
      AND pg_get_constraintdef(oid) LIKE '%status%';
    IF cname IS NOT NULL THEN
        EXECUTE format('ALTER TABLE mfg_orders DROP CONSTRAINT %I', cname);
    END IF;
    ALTER TABLE mfg_orders ADD CONSTRAINT mfg_orders_status_check
        CHECK (status IN ('Draft','Confirmed','InProcess','Completed','Closed','Posted','Cancelled'));
END $$;

CREATE INDEX IF NOT EXISTS ix_mfg_orders_company_status
    ON mfg_orders(company_id, status, doc_date) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_mfg_orders_company_bom
    ON mfg_orders(company_id, bom_id) WHERE bom_id IS NOT NULL AND is_deleted = FALSE;

-- ---- Staged workflow columns on mfg_order_lines ----------------------------------
ALTER TABLE mfg_order_lines ADD COLUMN IF NOT EXISTS line_no INTEGER NOT NULL DEFAULT 0;
ALTER TABLE mfg_order_lines ADD COLUMN IF NOT EXISTS consumed_qty NUMERIC NOT NULL DEFAULT 0;
ALTER TABLE mfg_order_lines ADD COLUMN IF NOT EXISTS produced_qty NUMERIC NOT NULL DEFAULT 0;
ALTER TABLE mfg_order_lines ADD COLUMN IF NOT EXISTS warehouse_code TEXT;
ALTER TABLE mfg_order_lines ADD COLUMN IF NOT EXISTS cost_share_pct NUMERIC NOT NULL DEFAULT 0 CHECK (cost_share_pct >= 0 AND cost_share_pct <= 100);
UPDATE mfg_order_lines SET line_no = sub.rn FROM (
    SELECT id, ROW_NUMBER() OVER (PARTITION BY order_id ORDER BY item_code) AS rn
    FROM mfg_order_lines) sub WHERE mfg_order_lines.id = sub.id AND mfg_order_lines.line_no = 0;

-- ---- Production events -------------------------------------------------------------
CREATE TABLE IF NOT EXISTS mfg_production_events (
    id uuid PRIMARY KEY,
    company_id TEXT NOT NULL,
    order_id uuid NOT NULL REFERENCES mfg_orders(id),
    event_type TEXT NOT NULL CHECK (event_type IN ('Confirm','Issue','Finish','Close','Cancel')),
    event_date TEXT NOT NULL,
    seq INTEGER NOT NULL DEFAULT 0,
    qty NUMERIC NOT NULL DEFAULT 0,
    amount NUMERIC NOT NULL DEFAULT 0,
    journal_doc_no TEXT,
    notes TEXT,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_mfg_production_events_order
    ON mfg_production_events(company_id, order_id, seq);

-- ---- Planning (MRP-lite) --------------------------------------------------------------
CREATE TABLE IF NOT EXISTS mfg_planning_runs (
    id uuid PRIMARY KEY,
    company_id TEXT NOT NULL,
    run_date TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'Completed',
    notes TEXT,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_mfg_planning_runs_company
    ON mfg_planning_runs(company_id, created_at);

CREATE TABLE IF NOT EXISTS mfg_planned_orders (
    id uuid PRIMARY KEY,
    run_id uuid NOT NULL REFERENCES mfg_planning_runs(id),
    company_id TEXT NOT NULL,
    material_code TEXT NOT NULL,
    material_name TEXT,
    qty NUMERIC NOT NULL CHECK (qty > 0),
    unit_code TEXT NOT NULL DEFAULT 'PCS',
    bom_id uuid,
    due_date TEXT,
    source TEXT,
    status TEXT NOT NULL DEFAULT 'Planned'
        CHECK (status IN ('Planned','Converted','Cancelled')),
    converted_order_id uuid,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_mfg_planned_orders_run
    ON mfg_planned_orders(company_id, run_id, status);

CREATE TABLE IF NOT EXISTS mfg_purchase_suggestions (
    id uuid PRIMARY KEY,
    run_id uuid NOT NULL REFERENCES mfg_planning_runs(id),
    company_id TEXT NOT NULL,
    material_code TEXT NOT NULL,
    material_name TEXT,
    qty NUMERIC NOT NULL CHECK (qty > 0),
    unit_code TEXT,
    source TEXT,
    status TEXT NOT NULL DEFAULT 'Suggested'
        CHECK (status IN ('Suggested','Converted','Cancelled')),
    converted_doc_id INTEGER,
    converted_doc_no TEXT,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_mfg_purchase_suggestions_run
    ON mfg_purchase_suggestions(company_id, run_id, status);

-- ---- Permissions (display_order continues at 366) --------------------------------------
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('MFG.PLANNING.VIEW','Manufacturing Planning - View','MANUFACTURING','PLANNING','VIEW','Manufacturing Planning - View','Manufacturing Planning - View','View manufacturing planning runs and suggestions (Build 058)',366,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('MFG.PLANNING.RUN','Manufacturing Planning - Run','MANUFACTURING','PLANNING','RUN','Manufacturing Planning - Run','Manufacturing Planning - Run','Run MRP-lite planning (Build 058)',367,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('MFG.PLANNING.CONVERT','Manufacturing Planning - Convert','MANUFACTURING','PLANNING','CONVERT','Manufacturing Planning - Convert','Manufacturing Planning - Convert','Convert planned orders and purchase suggestions into real documents (Build 058)',368,true) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('MFG.PLANNING.VIEW','MFG.PLANNING.RUN','MFG.PLANNING.CONVERT') ON CONFLICT DO NOTHING;
INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('MFG.PLANNING.VIEW','MFG.PLANNING.RUN','MFG.PLANNING.CONVERT') ON CONFLICT DO NOTHING;
INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_VIEWER' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('MFG.PLANNING.VIEW') ON CONFLICT DO NOTHING;
