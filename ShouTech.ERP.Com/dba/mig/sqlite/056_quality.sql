-- ============================================================================
-- 056 - Quality management (SQLite)
--
-- Phase B design section 5 (migration 056): quality parameters,
-- templates, inspections and alerts; the quarantine-warehouse concept
-- (owner decision Q2: rejected / awaiting-inspection stock lives in a
-- dedicated quarantine warehouse, valued and visible, never sellable);
-- and the per-company QC_GATE_MODE setting (owner decision Q1: the
-- setting is per company; the seeded default is Warn, the go-live
-- mode both competitors recommend - a company switches to Stop when
-- it is ready for the hard gate).
--
-- GATE MODEL (the task mandate's quarantine flow, reconciling the
-- design's Stop/Warn vocabulary with its disposition section):
--   * A receipt (purchase / stock-in / opening / manufacturing
--     completion) of a material with quality_check_required = 1 lands
--     in the company's QUARANTINE warehouse - not in sellable stock -
--     and a Draft inspection is created against the receipt in the
--     same transaction (QualityGate, src/ShouTech.Infrastructure).
--   * Completing the inspection releases the accepted quantity to the
--     document warehouse through the valuation engine (a value-exact
--     transfer pair, so batches/serials from 054 keep their identity)
--     and dispositions the rejected quantity (ReturnToSupplier /
--     Scrap / Rework / UseAsIs / Quarantine - design section 5.2).
--   * QC_GATE_MODE decides what happens when someone tries to SELL
--     stock that only exists in quarantine: Stop refuses the sale
--     (INV-6012, insufficient sellable stock); Warn lets the sale draw
--     the shortfall from quarantine and records a GateWarning alert
--     (the design's Warn semantics: post + surface). Outgoing
--     inspections (decision Q4, included) gate delivery postings the
--     same way: Stop blocks without an accepted outgoing inspection,
--     Warn posts and raises the pending inspection.
--
-- GROUND-TRUTH ADAPTATIONS vs the design sketch (documented in
-- docs/BUILD_PHASEB_056.md):
--   * Quality lives in the STOCK world, so company_id is the company
--     CODE (TEXT), exactly like materials / stock_batches (054) - not
--     the journal world's Guid keying that budgets (055) use.
--   * quality_inspections gains: template_id (which template the
--     lines were snapshotted from), total_qty (the full received
--     quantity under inspection; sample_qty stays the inspected
--     sample, defaulting to total_qty), quarantine_warehouse_id +
--     warehouse_id as the release pair (where the stock sits / where
--     accepted stock goes), party_id (the receipt's supplier - the
--     supplier reject-rate report needs it on the inspection, the
--     design carries it only on alerts), and due_date (the pending
--     worklist and the overdue-inspection alert need a deadline; the
--     design gives due dates only to alerts).
--   * quality_template_lines / quality_inspection_lines /
--     quality_inspection_readings gain surrogate TEXT primary keys
--     (the sketch keys them only by parent); readings and the
--     completion transaction address rows by id.
--   * quality_alerts gains alert_type (FailedInspection /
--     OverdueInspection / GateWarning) and message, so the three
--     alert sources the gate produces are distinguishable in one list.
--   * QC_GATE_MODE is stored in a dedicated quality_settings table:
--     no generic per-company settings table exists in the schema, and
--     inventing one for a single setting would be the larger change.
--   * Formula-based criteria are NOT built (decision Q3: deferred);
--     param_type is Numeric / Value / PassFail only.
--
-- NUMBERING NOTE: like 053-055, this file sorts before the
-- already-shipped 059-064 files; the migrator applies pending
-- migrations in filename order and skips by checksum. 056 depends on
-- 013/016-era objects (materials, warehouses) plus roles/permissions
-- (002-004), never on 059-064 objects.
--
-- Idempotent: table/index creation is IF NOT EXISTS, column adds are
-- guarded by the migrator directive, permission seeds are INSERT OR
-- IGNORE + UPDATE, and the checksum ledger skips a re-run.
-- ============================================================================

-- ---- Per-company gate mode (decision Q1) ------------------------------------
CREATE TABLE IF NOT EXISTS quality_settings (
    company_id TEXT NOT NULL PRIMARY KEY,
    gate_mode TEXT NOT NULL DEFAULT 'Warn'
        CHECK (gate_mode IN ('Stop','Warn')),
    updated_at TEXT,
    updated_by TEXT
);

-- ---- Parameters (measurable characteristics) ---------------------------------
CREATE TABLE IF NOT EXISTS quality_parameters (
    param_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL DEFAULT '',
    name_ar TEXT NOT NULL DEFAULT '',
    param_type TEXT NOT NULL DEFAULT 'Numeric'
        CHECK (param_type IN ('Numeric','Value','PassFail')),
    unit TEXT,
    group_name TEXT,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT,
    created_by TEXT,
    updated_by TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_quality_parameters_company_code
    ON quality_parameters(company_id, code) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_quality_parameters_company
    ON quality_parameters(company_id, is_active) WHERE is_deleted = 0;

-- ---- Templates -----------------------------------------------------------------
CREATE TABLE IF NOT EXISTS quality_templates (
    template_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL DEFAULT '',
    name_ar TEXT NOT NULL DEFAULT '',
    inspection_type TEXT NOT NULL DEFAULT 'Incoming'
        CHECK (inspection_type IN ('Incoming','InProcess','Outgoing')),
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT,
    created_by TEXT,
    updated_by TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_quality_templates_company_code
    ON quality_templates(company_id, code) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_quality_templates_company
    ON quality_templates(company_id, inspection_type, is_active) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS quality_template_lines (
    template_line_id TEXT NOT NULL PRIMARY KEY,
    template_id TEXT NOT NULL,
    param_id TEXT NOT NULL,
    min_value NUMERIC,
    max_value NUMERIC,
    accepted_value TEXT,
    is_mandatory INTEGER NOT NULL DEFAULT 1,
    sort INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_quality_template_lines_template
    ON quality_template_lines(template_id, sort);
CREATE UNIQUE INDEX IF NOT EXISTS ux_quality_template_lines_param
    ON quality_template_lines(template_id, param_id);

-- ---- Inspections ------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS quality_inspections (
    inspection_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    inspection_type TEXT NOT NULL DEFAULT 'Incoming'
        CHECK (inspection_type IN ('Incoming','InProcess','Outgoing')),
    template_id TEXT,
    ref_doc_kind TEXT NOT NULL DEFAULT '',
    ref_doc_id TEXT NOT NULL DEFAULT '',
    ref_doc_no TEXT NOT NULL DEFAULT '',
    material_id INTEGER NOT NULL,
    batch_id INTEGER,
    warehouse_id INTEGER NOT NULL DEFAULT 0,
    quarantine_warehouse_id INTEGER,
    party_id INTEGER,
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
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT,
    created_by TEXT,
    updated_by TEXT
);
CREATE INDEX IF NOT EXISTS ix_quality_inspections_pending
    ON quality_inspections(company_id, status, due_date) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_quality_inspections_doc
    ON quality_inspections(company_id, ref_doc_kind, ref_doc_no, material_id)
    WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_quality_inspections_material
    ON quality_inspections(company_id, material_id, status) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS quality_inspection_lines (
    line_id TEXT NOT NULL PRIMARY KEY,
    inspection_id TEXT NOT NULL,
    param_id TEXT NOT NULL,
    min_value NUMERIC,
    max_value NUMERIC,
    accepted_value TEXT,
    is_mandatory INTEGER NOT NULL DEFAULT 1,
    result TEXT
        CHECK (result IS NULL OR result IN ('Pass','Fail')),
    is_overridden INTEGER NOT NULL DEFAULT 0,
    notes TEXT,
    sort INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_quality_inspection_lines_inspection
    ON quality_inspection_lines(inspection_id, sort);

CREATE TABLE IF NOT EXISTS quality_inspection_readings (
    reading_id TEXT NOT NULL PRIMARY KEY,
    line_id TEXT NOT NULL,
    reading_no INTEGER NOT NULL CHECK (reading_no BETWEEN 1 AND 3),
    reading_num NUMERIC,
    reading_text TEXT,
    UNIQUE(line_id, reading_no)
);
CREATE INDEX IF NOT EXISTS ix_quality_inspection_readings_line
    ON quality_inspection_readings(line_id, reading_no);

-- ---- Alerts (Odoo pattern: every failure raises a tracked alert) ------------------
CREATE TABLE IF NOT EXISTS quality_alerts (
    alert_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    alert_type TEXT NOT NULL DEFAULT 'FailedInspection'
        CHECK (alert_type IN ('FailedInspection','OverdueInspection','GateWarning')),
    inspection_id TEXT,
    material_id INTEGER,
    batch_id INTEGER,
    party_id INTEGER,
    message TEXT,
    root_cause TEXT,
    corrective_action TEXT,
    preventive_action TEXT,
    status TEXT NOT NULL DEFAULT 'Open'
        CHECK (status IN ('Open','InProgress','Closed')),
    owner TEXT,
    due_date TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT,
    created_by TEXT,
    updated_by TEXT
);
CREATE INDEX IF NOT EXISTS ix_quality_alerts_company_status
    ON quality_alerts(company_id, status, due_date) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_quality_alerts_inspection
    ON quality_alerts(inspection_id) WHERE is_deleted = 0;

-- ---- Material / warehouse QC columns ----------------------------------------------
-- @if-column-missing materials.qc_template_in_id
ALTER TABLE materials ADD COLUMN qc_template_in_id TEXT;
-- @if-column-missing materials.qc_template_out_id
ALTER TABLE materials ADD COLUMN qc_template_out_id TEXT;
-- @if-column-missing materials.qc_outgoing_required
ALTER TABLE materials ADD COLUMN qc_outgoing_required INTEGER NOT NULL DEFAULT 0;
-- @if-column-missing warehouses.is_quarantine
ALTER TABLE warehouses ADD COLUMN is_quarantine INTEGER NOT NULL DEFAULT 0;

-- ---- Permissions (028/038/053/054/055 pattern) --------------------------------------
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('QUALITY.PARAM.VIEW','Quality Parameter - View','QUALITY','PARAM','VIEW','Quality Parameter - View','Quality Parameter - View','View quality inspection parameters (Phase B 056)',349,0);
UPDATE permissions SET group_code='QUALITY',feature_code='PARAM',operation_code='VIEW',name_ar='Quality Parameter - View',name_en='Quality Parameter - View',display_order=349,is_sensitive=0 WHERE permission_code='QUALITY.PARAM.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('QUALITY.PARAM.EDIT','Quality Parameter - Edit','QUALITY','PARAM','EDIT','Quality Parameter - Edit','Quality Parameter - Edit','Create and edit quality inspection parameters (Phase B 056)',350,0);
UPDATE permissions SET group_code='QUALITY',feature_code='PARAM',operation_code='EDIT',name_ar='Quality Parameter - Edit',name_en='Quality Parameter - Edit',display_order=350,is_sensitive=0 WHERE permission_code='QUALITY.PARAM.EDIT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('QUALITY.TEMPLATE.VIEW','Quality Template - View','QUALITY','TEMPLATE','VIEW','Quality Template - View','Quality Template - View','View quality inspection templates (Phase B 056)',351,0);
UPDATE permissions SET group_code='QUALITY',feature_code='TEMPLATE',operation_code='VIEW',name_ar='Quality Template - View',name_en='Quality Template - View',display_order=351,is_sensitive=0 WHERE permission_code='QUALITY.TEMPLATE.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('QUALITY.TEMPLATE.EDIT','Quality Template - Edit','QUALITY','TEMPLATE','EDIT','Quality Template - Edit','Quality Template - Edit','Create and edit quality inspection templates and material assignments (Phase B 056)',352,0);
UPDATE permissions SET group_code='QUALITY',feature_code='TEMPLATE',operation_code='EDIT',name_ar='Quality Template - Edit',name_en='Quality Template - Edit',display_order=352,is_sensitive=0 WHERE permission_code='QUALITY.TEMPLATE.EDIT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('QUALITY.INSPECT.VIEW','Quality Inspection - View','QUALITY','INSPECT','VIEW','Quality Inspection - View','Quality Inspection - View','View quality inspections and the pending-inspection worklist (Phase B 056)',353,0);
UPDATE permissions SET group_code='QUALITY',feature_code='INSPECT',operation_code='VIEW',name_ar='Quality Inspection - View',name_en='Quality Inspection - View',display_order=353,is_sensitive=0 WHERE permission_code='QUALITY.INSPECT.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('QUALITY.INSPECT.EDIT','Quality Inspection - Record Results','QUALITY','INSPECT','EDIT','Quality Inspection - Record Results','Quality Inspection - Record Results','Record inspection readings and results (Phase B 056)',354,0);
UPDATE permissions SET group_code='QUALITY',feature_code='INSPECT',operation_code='EDIT',name_ar='Quality Inspection - Record Results',name_en='Quality Inspection - Record Results',display_order=354,is_sensitive=0 WHERE permission_code='QUALITY.INSPECT.EDIT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('QUALITY.INSPECT.COMPLETE','Quality Inspection - Complete and Release','QUALITY','INSPECT','COMPLETE','Quality Inspection - Complete and Release','Quality Inspection - Complete and Release','Complete an inspection: release accepted stock from quarantine and disposition rejected stock (Phase B 056)',355,1);
UPDATE permissions SET group_code='QUALITY',feature_code='INSPECT',operation_code='COMPLETE',name_ar='Quality Inspection - Complete and Release',name_en='Quality Inspection - Complete and Release',display_order=355,is_sensitive=1 WHERE permission_code='QUALITY.INSPECT.COMPLETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('QUALITY.ALERT.VIEW','Quality Alert - View','QUALITY','ALERT','VIEW','Quality Alert - View','Quality Alert - View','View quality alerts (failed, overdue and gate-warning) (Phase B 056)',356,0);
UPDATE permissions SET group_code='QUALITY',feature_code='ALERT',operation_code='VIEW',name_ar='Quality Alert - View',name_en='Quality Alert - View',display_order=356,is_sensitive=0 WHERE permission_code='QUALITY.ALERT.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('QUALITY.ALERT.EDIT','Quality Alert - Manage','QUALITY','ALERT','EDIT','Quality Alert - Manage','Quality Alert - Manage','Work and close quality alerts with root cause and corrective action (Phase B 056)',357,0);
UPDATE permissions SET group_code='QUALITY',feature_code='ALERT',operation_code='EDIT',name_ar='Quality Alert - Manage',name_en='Quality Alert - Manage',display_order=357,is_sensitive=0 WHERE permission_code='QUALITY.ALERT.EDIT';

INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('QUALITY.PARAM.VIEW','QUALITY.PARAM.EDIT','QUALITY.TEMPLATE.VIEW','QUALITY.TEMPLATE.EDIT','QUALITY.INSPECT.VIEW','QUALITY.INSPECT.EDIT','QUALITY.INSPECT.COMPLETE','QUALITY.ALERT.VIEW','QUALITY.ALERT.EDIT');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('QUALITY.PARAM.VIEW','QUALITY.TEMPLATE.VIEW','QUALITY.INSPECT.VIEW','QUALITY.INSPECT.EDIT','QUALITY.INSPECT.COMPLETE','QUALITY.ALERT.VIEW','QUALITY.ALERT.EDIT');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_VIEWER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('QUALITY.PARAM.VIEW','QUALITY.TEMPLATE.VIEW','QUALITY.INSPECT.VIEW','QUALITY.ALERT.VIEW');
