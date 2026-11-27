-- @shoutech-migration 034_manufacturing.sql 56747F351C2D8FE272951D5C05461EB6A5C8C37E4619D2C5B6A3C77C447A65C9 56747F351C2D8FE272951D5C05461EB6A5C8C37E4619D2C5B6A3C77C447A65C9
-- PostgreSQL counterpart of sqlite/034_manufacturing.sql (Manufacturing, RS-8).
-- Money/quantities use NUMERIC on PostgreSQL; ids are uuid per 026 style.

CREATE TABLE IF NOT EXISTS mfg_orders (
    id uuid PRIMARY KEY,
    company_id TEXT NOT NULL,
    doc_no INTEGER NOT NULL,
    doc_date TEXT NOT NULL,
    item_code TEXT NOT NULL,
    item_name TEXT,
    qty NUMERIC NOT NULL CHECK (qty > 0),
    unit_code TEXT NOT NULL DEFAULT 'PCS',
    warehouse_code TEXT,
    overhead_amount NUMERIC NOT NULL DEFAULT 0 CHECK (overhead_amount >= 0),
    standard_unit_cost NUMERIC NOT NULL DEFAULT 0 CHECK (standard_unit_cost >= 0),
    total_cost NUMERIC NOT NULL DEFAULT 0,
    unit_cost NUMERIC NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Draft'
        CHECK (status IN ('Draft','Posted','Cancelled')),
    notes TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_mfg_orders_company_docno
    ON mfg_orders(company_id, doc_no) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS mfg_order_lines (
    id uuid PRIMARY KEY,
    order_id uuid NOT NULL REFERENCES mfg_orders(id),
    line_kind TEXT NOT NULL DEFAULT 'RAW',
    item_code TEXT NOT NULL,
    item_name TEXT,
    qty NUMERIC NOT NULL CHECK (qty > 0),
    unit_code TEXT,
    unit_cost NUMERIC NOT NULL DEFAULT 0 CHECK (unit_cost >= 0),
    standard_unit_cost NUMERIC NOT NULL DEFAULT 0 CHECK (standard_unit_cost >= 0),
    amount NUMERIC NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS ix_mfg_order_lines_order
    ON mfg_order_lines(order_id);
-- @shoutech-migration 056_quality.sql 939F949899A2565CCE2B9CA905ACEC6B4AF08FF5889C88F56C36CF24718716B5 939F949899A2565CCE2B9CA905ACEC6B4AF08FF5889C88F56C36CF24718716B5
-- ============================================================================
-- 056 - Quality management (PostgreSQL)
-- PostgreSQL counterpart of sqlite/056_quality.sql (Phase B design
-- section 5). See the SQLite file for the gate model, the Q1/Q2/Q3/Q4
-- decisions, the ground-truth adaptations (stock-world company CODE
-- keying, template_id / total_qty / release-pair warehouses / party_id
-- / due_date on inspections, surrogate line keys, alert_type/message,
-- the dedicated quality_settings table) and the numbering note (056
-- applies cleanly after 059-064). Ids are uuid on this provider (the
-- budgets 055 pattern); stock-world references (material / warehouse /
-- batch) are BIGINT, matching the BIGSERIAL masters.
-- ============================================================================

-- ---- Per-company gate mode (decision Q1) ------------------------------------
CREATE TABLE IF NOT EXISTS quality_settings (
    company_id TEXT NOT NULL PRIMARY KEY,
    gate_mode TEXT NOT NULL DEFAULT 'Warn'
        CHECK (gate_mode IN ('Stop','Warn')),
    updated_at TIMESTAMPTZ,
    updated_by TEXT
);

-- ---- Parameters (measurable characteristics) ---------------------------------
CREATE TABLE IF NOT EXISTS quality_parameters (
    param_id uuid NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL DEFAULT '',
    name_ar TEXT NOT NULL DEFAULT '',
    param_type TEXT NOT NULL DEFAULT 'Numeric'
        CHECK (param_type IN ('Numeric','Value','PassFail')),
    unit TEXT,
    group_name TEXT,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_quality_parameters_company_code
    ON quality_parameters(company_id, code) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_quality_parameters_company
    ON quality_parameters(company_id, is_active) WHERE is_deleted = FALSE;

-- ---- Templates -----------------------------------------------------------------
CREATE TABLE IF NOT EXISTS quality_templates (
    template_id uuid NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL DEFAULT '',
    name_ar TEXT NOT NULL DEFAULT '',
    inspection_type TEXT NOT NULL DEFAULT 'Incoming'
        CHECK (inspection_type IN ('Incoming','InProcess','Outgoing')),
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_quality_templates_company_code
    ON quality_templates(company_id, code) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_quality_templates_company
    ON quality_templates(company_id, inspection_type, is_active) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS quality_template_lines (
    template_line_id uuid NOT NULL PRIMARY KEY,
    template_id uuid NOT NULL,
    param_id uuid NOT NULL,
    min_value NUMERIC,
    max_value NUMERIC,
    accepted_value TEXT,
    is_mandatory BOOLEAN NOT NULL DEFAULT TRUE,
    sort INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_quality_template_lines_template
    ON quality_template_lines(template_id, sort);
CREATE UNIQUE INDEX IF NOT EXISTS ux_quality_template_lines_param
    ON quality_template_lines(template_id, param_id);

-- ---- Inspections ------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS quality_inspections (
    inspection_id uuid NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    inspection_type TEXT NOT NULL DEFAULT 'Incoming'
        CHECK (inspection_type IN ('Incoming','InProcess','Outgoing')),
    template_id uuid,
    ref_doc_kind TEXT NOT NULL DEFAULT '',
    ref_doc_id TEXT NOT NULL DEFAULT '',
    ref_doc_no TEXT NOT NULL DEFAULT '',
    material_id BIGINT NOT NULL,
    batch_id BIGINT,
    warehouse_id BIGINT NOT NULL DEFAULT 0,
    quarantine_warehouse_id BIGINT,
    party_id BIGINT,
    total_qty NUMERIC NOT NULL DEFAULT 0,
    sample_qty NUMERIC NOT NULL DEFAULT 0,
    accepted_qty NUMERIC NOT NULL DEFAULT 0,
    rejected_qty NUMERIC NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Draft'
        CHECK (status IN ('Draft','Accepted','PartiallyAccepted','Rejected')),
    disposition TEXT
        CHECK (disposition IS NULL OR disposition IN
            ('ReturnToSupplier','Scrap','Rework','UseAsIs','Quarantine')),
    inspector TEXT,
    inspected_at TEXT,
    due_date TEXT,
    notes TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT
);
CREATE INDEX IF NOT EXISTS ix_quality_inspections_pending
    ON quality_inspections(company_id, status, due_date) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_quality_inspections_doc
    ON quality_inspections(company_id, ref_doc_kind, ref_doc_no, material_id)
    WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_quality_inspections_material
    ON quality_inspections(company_id, material_id, status) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS quality_inspection_lines (
    line_id uuid NOT NULL PRIMARY KEY,
    inspection_id uuid NOT NULL,
    param_id uuid NOT NULL,
    min_value NUMERIC,
    max_value NUMERIC,
    accepted_value TEXT,
    is_mandatory BOOLEAN NOT NULL DEFAULT TRUE,
    result TEXT
        CHECK (result IS NULL OR result IN ('Pass','Fail')),
    is_overridden BOOLEAN NOT NULL DEFAULT FALSE,
    notes TEXT,
    sort INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_quality_inspection_lines_inspection
    ON quality_inspection_lines(inspection_id, sort);

CREATE TABLE IF NOT EXISTS quality_inspection_readings (
    reading_id uuid NOT NULL PRIMARY KEY,
    line_id uuid NOT NULL,
    reading_no INTEGER NOT NULL CHECK (reading_no BETWEEN 1 AND 3),
    reading_num NUMERIC,
    reading_text TEXT,
    UNIQUE(line_id, reading_no)
);
CREATE INDEX IF NOT EXISTS ix_quality_inspection_readings_line
    ON quality_inspection_readings(line_id, reading_no);

-- ---- Alerts ------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS quality_alerts (
    alert_id uuid NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    alert_type TEXT NOT NULL DEFAULT 'FailedInspection'
        CHECK (alert_type IN ('FailedInspection','OverdueInspection','GateWarning')),
    inspection_id uuid,
    material_id BIGINT,
    batch_id BIGINT,
    party_id BIGINT,
    message TEXT,
    root_cause TEXT,
    corrective_action TEXT,
    preventive_action TEXT,
    status TEXT NOT NULL DEFAULT 'Open'
        CHECK (status IN ('Open','InProgress','Closed')),
    owner TEXT,
    due_date TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT
);
CREATE INDEX IF NOT EXISTS ix_quality_alerts_company_status
    ON quality_alerts(company_id, status, due_date) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_quality_alerts_inspection
    ON quality_alerts(inspection_id) WHERE is_deleted = FALSE;

-- ---- Material / warehouse QC columns ----------------------------------------------
ALTER TABLE materials ADD COLUMN IF NOT EXISTS qc_template_in_id TEXT;
ALTER TABLE materials ADD COLUMN IF NOT EXISTS qc_template_out_id TEXT;
ALTER TABLE materials ADD COLUMN IF NOT EXISTS qc_outgoing_required INTEGER NOT NULL DEFAULT 0;
ALTER TABLE warehouses ADD COLUMN IF NOT EXISTS is_quarantine INTEGER NOT NULL DEFAULT 0;

-- ---- Permissions (028/038/053/054/055 pattern) --------------------------------------
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('QUALITY.PARAM.VIEW','Quality Parameter - View','QUALITY','PARAM','VIEW','Quality Parameter - View','Quality Parameter - View','View quality inspection parameters (Phase B 056)',349,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('QUALITY.PARAM.EDIT','Quality Parameter - Edit','QUALITY','PARAM','EDIT','Quality Parameter - Edit','Quality Parameter - Edit','Create and edit quality inspection parameters (Phase B 056)',350,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('QUALITY.TEMPLATE.VIEW','Quality Template - View','QUALITY','TEMPLATE','VIEW','Quality Template - View','Quality Template - View','View quality inspection templates (Phase B 056)',351,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('QUALITY.TEMPLATE.EDIT','Quality Template - Edit','QUALITY','TEMPLATE','EDIT','Quality Template - Edit','Quality Template - Edit','Create and edit quality inspection templates and material assignments (Phase B 056)',352,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('QUALITY.INSPECT.VIEW','Quality Inspection - View','QUALITY','INSPECT','VIEW','Quality Inspection - View','Quality Inspection - View','View quality inspections and the pending-inspection worklist (Phase B 056)',353,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('QUALITY.INSPECT.EDIT','Quality Inspection - Record Results','QUALITY','INSPECT','EDIT','Quality Inspection - Record Results','Quality Inspection - Record Results','Record inspection readings and results (Phase B 056)',354,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('QUALITY.INSPECT.COMPLETE','Quality Inspection - Complete and Release','QUALITY','INSPECT','COMPLETE','Quality Inspection - Complete and Release','Quality Inspection - Complete and Release','Complete an inspection: release accepted stock from quarantine and disposition rejected stock (Phase B 056)',355,true) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('QUALITY.ALERT.VIEW','Quality Alert - View','QUALITY','ALERT','VIEW','Quality Alert - View','Quality Alert - View','View quality alerts (failed, overdue and gate-warning) (Phase B 056)',356,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('QUALITY.ALERT.EDIT','Quality Alert - Manage','QUALITY','ALERT','EDIT','Quality Alert - Manage','Quality Alert - Manage','Work and close quality alerts with root cause and corrective action (Phase B 056)',357,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('QUALITY.PARAM.VIEW','QUALITY.PARAM.EDIT','QUALITY.TEMPLATE.VIEW','QUALITY.TEMPLATE.EDIT','QUALITY.INSPECT.VIEW','QUALITY.INSPECT.EDIT','QUALITY.INSPECT.COMPLETE','QUALITY.ALERT.VIEW','QUALITY.ALERT.EDIT') ON CONFLICT DO NOTHING;
INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('QUALITY.PARAM.VIEW','QUALITY.TEMPLATE.VIEW','QUALITY.INSPECT.VIEW','QUALITY.INSPECT.EDIT','QUALITY.INSPECT.COMPLETE','QUALITY.ALERT.VIEW','QUALITY.ALERT.EDIT') ON CONFLICT DO NOTHING;
INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_VIEWER' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('QUALITY.PARAM.VIEW','QUALITY.TEMPLATE.VIEW','QUALITY.INSPECT.VIEW','QUALITY.ALERT.VIEW') ON CONFLICT DO NOTHING;
-- @shoutech-migration 058_manufacturing_bom_planning.sql A66DA776B33D525BF2B37DE5231C573DBC2524A88523A3C4E1CAB7895E05F7D4 A66DA776B33D525BF2B37DE5231C573DBC2524A88523A3C4E1CAB7895E05F7D4
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
