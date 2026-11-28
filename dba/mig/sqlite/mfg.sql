-- @shoutech-migration 034_manufacturing.sql E9747D8D36D33BA7C97812130B0C01D9DF2DB684A1324BD74749050277BE3879 3D774BA679DB450DDA55E1C361502D9DFC877788442AE8CBC205A597A6665013 3D774BA679DB450DDA55E1C361502D9DFC877788442AE8CBC205A597A6665013 3D774BA679DB450DDA55E1C361502D9DFC877788442AE8CBC205A597A6665013
-- Owns Manufacturing orders (RS-8): mfg_orders + mfg_order_lines.
-- Posting consumes raw materials and produces the finished item through
-- material_stock_movements in one transaction (see MfgOrderRepository).
-- Dates are stored as ISO text (yyyy-MM-dd) on both providers.
-- Runtime services must not create tables.

CREATE TABLE IF NOT EXISTS mfg_orders (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    doc_no INTEGER NOT NULL,
    doc_date TEXT NOT NULL,
    item_code TEXT NOT NULL,
    item_name TEXT,
    qty REAL NOT NULL CHECK (qty > 0),
    unit_code TEXT NOT NULL DEFAULT 'PCS',
    warehouse_code TEXT,
    overhead_amount REAL NOT NULL DEFAULT 0 CHECK (overhead_amount >= 0),
    standard_unit_cost REAL NOT NULL DEFAULT 0 CHECK (standard_unit_cost >= 0),
    total_cost REAL NOT NULL DEFAULT 0,
    unit_cost REAL NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Draft'
        CHECK (status IN ('Draft','Posted','Cancelled')),
    notes TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_mfg_orders_company_docno
    ON mfg_orders(company_id, doc_no) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS mfg_order_lines (
    id TEXT PRIMARY KEY,
    order_id TEXT NOT NULL REFERENCES mfg_orders(id),
    line_kind TEXT NOT NULL DEFAULT 'RAW',
    item_code TEXT NOT NULL,
    item_name TEXT,
    qty REAL NOT NULL CHECK (qty > 0),
    unit_code TEXT,
    unit_cost REAL NOT NULL DEFAULT 0 CHECK (unit_cost >= 0),
    standard_unit_cost REAL NOT NULL DEFAULT 0 CHECK (standard_unit_cost >= 0),
    amount REAL NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS ix_mfg_order_lines_order
    ON mfg_order_lines(order_id);
-- @shoutech-migration 056_quality.sql B8D6044C64D43DFDBE7FCA8CC3C9481AD61715B303D88FDA2A6B4E21B30E1897 1B5036818BB91E5D23CAC3D408F36AF60F40118550CEBD2E2099794ED98E723F 1B5036818BB91E5D23CAC3D408F36AF60F40118550CEBD2E2099794ED98E723F 1B5036818BB91E5D23CAC3D408F36AF60F40118550CEBD2E2099794ED98E723F
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
-- @shoutech-migration 058_manufacturing_bom_planning.sql 08F304D5D4229C7B32143E51126182AEABC304E9519A81A855D012A3A54F0FC8 D9C7541351D944F6B5A478456EB01303CA0D6AAC7142A03B6ABE8FB2D9B972C8 D9C7541351D944F6B5A478456EB01303CA0D6AAC7142A03B6ABE8FB2D9B972C8 D9C7541351D944F6B5A478456EB01303CA0D6AAC7142A03B6ABE8FB2D9B972C8
-- ============================================================================
-- 058 - Manufacturing build-out: BOM master, staged workflow, WIP/variance
--       GL support and MRP-lite planning (SQLite)
--
-- Build/manufacturing (see docs/BUILD_MANUFACTURING.md). Ground truth this
-- migration builds on: migration 034 owns mfg_orders / mfg_order_lines
-- (company keyed by company CODE, TEXT); the valuation engine (053) owns
-- stock valuation; batch/serial (054) owns allocations; GlPosting (Step 3)
-- owns document GL. This migration adds:
--   * mfg_boms / mfg_bom_lines: the versioned BOM master. A component may
--     itself have a BOM (multi-level); explosion is cycle-safe in
--     BomRepository (catalog code MFG-1001).
--   * mfg_orders / mfg_order_lines staged-workflow columns: BOM link,
--     planned dates, sales-order link, cost center, split warehouses
--     (source / WIP / target / scrap), produced quantity, accumulated
--     cost, WIP balance, variance; per-line required vs consumed,
--     per-line source warehouse, cost share for by-product lines, and a
--     stable line_no (fixes sweep MFG-9 line-order loss).
--   * mfg_production_events: one row per staged action (Confirm / Issue /
--     Finish / Close / Cancel) with the quantities, amounts and the real
--     GL document numbers GlPosting created — the audit trail the staged
--     workflow and the WIP report read.
--   * mfg_planning_runs / mfg_planned_orders / mfg_purchase_suggestions:
--     MRP-lite planning output; conversions stamp the real document ids.
--   * The mfg_orders status CHECK is widened to the staged lifecycle
--     (Draft / Confirmed / InProcess / Completed / Closed / Posted /
--     Cancelled) by a full table rebuild of both manufacturing tables
--     (052 precedent). The rebuild renames the old tables away first so
--     foreign keys re-point step by step and no DROP ever targets a
--     still-referenced table.
--
-- GL ACCOUNT CODES (canonical templates, cod/app/src/Resources/COA):
--   WIP 1211 "Work in Progress Inventory" (industrial / job-orders /
--   continuous-inventory templates), Finished Goods 1212, Variance 406
--   "Inventory Variances and Adjustments", Applied Overhead 409. The
--   posting code resolves each with the Step-3 optional-leg pattern and
--   falls back to Inventory 1203 / COGS 401 when a company's chart has
--   no postable account under the manufacturing code.
--
-- NUMBERING NOTE: 058 was reserved while the foundations chain shipped
-- 059-064 first. The migrator applies pending migrations in filename
-- order and skips by checksum; 058 depends only on 034-era objects plus
-- roles/permissions (002-004), never on 059-064 objects.
--
-- Idempotent: the checksum ledger skips a re-run; inside the file every
-- CREATE is IF NOT EXISTS, column adds are guarded by the migrator
-- directive, permission seeds are INSERT OR IGNORE + UPDATE, and the
-- table rebuild is written so the data copy is a pure INSERT..SELECT
-- over columns that exist in both shapes.
-- ============================================================================

-- ---- BOM master ---------------------------------------------------------------
CREATE TABLE IF NOT EXISTS mfg_boms (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    material_code TEXT NOT NULL,
    material_name TEXT,
    version INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
    is_active INTEGER NOT NULL DEFAULT 1,
    is_default INTEGER NOT NULL DEFAULT 0,
    output_qty REAL NOT NULL DEFAULT 1 CHECK (output_qty > 0),
    output_unit TEXT NOT NULL DEFAULT 'PCS',
    notes TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_mfg_boms_company_material_version
    ON mfg_boms(company_id, material_code, version) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_mfg_boms_company_material
    ON mfg_boms(company_id, material_code, is_active, is_default) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS mfg_bom_lines (
    id TEXT PRIMARY KEY,
    bom_id TEXT NOT NULL REFERENCES mfg_boms(id),
    line_no INTEGER NOT NULL DEFAULT 0,
    component_code TEXT NOT NULL,
    component_name TEXT,
    qty REAL NOT NULL CHECK (qty > 0),
    unit_code TEXT,
    scrap_pct REAL NOT NULL DEFAULT 0 CHECK (scrap_pct >= 0 AND scrap_pct < 100),
    warehouse_code TEXT,
    standard_unit_cost REAL NOT NULL DEFAULT 0 CHECK (standard_unit_cost >= 0)
);
CREATE INDEX IF NOT EXISTS ix_mfg_bom_lines_bom
    ON mfg_bom_lines(bom_id, line_no);

-- ---- Staged workflow: rebuild mfg_orders + mfg_order_lines --------------------
DROP TABLE IF EXISTS mfg_order_lines_pre058;
DROP TABLE IF EXISTS mfg_orders_pre058;
-- Step 1: move the old tables aside (SQLite rewrites the lines table's
-- foreign key to the renamed orders table, so nothing references the
-- final names while the new tables are built).
ALTER TABLE mfg_orders RENAME TO mfg_orders_pre058;
ALTER TABLE mfg_order_lines RENAME TO mfg_order_lines_pre058;

CREATE TABLE mfg_orders (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    doc_no INTEGER NOT NULL,
    doc_date TEXT NOT NULL,
    item_code TEXT NOT NULL,
    item_name TEXT,
    qty REAL NOT NULL CHECK (qty > 0),
    unit_code TEXT NOT NULL DEFAULT 'PCS',
    warehouse_code TEXT,
    overhead_amount REAL NOT NULL DEFAULT 0 CHECK (overhead_amount >= 0),
    standard_unit_cost REAL NOT NULL DEFAULT 0 CHECK (standard_unit_cost >= 0),
    total_cost REAL NOT NULL DEFAULT 0,
    unit_cost REAL NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Draft'
        CHECK (status IN ('Draft','Confirmed','InProcess','Completed','Closed','Posted','Cancelled')),
    notes TEXT,
    bom_id TEXT,
    bom_version INTEGER,
    planned_start_date TEXT,
    planned_end_date TEXT,
    sales_order_doc_id INTEGER,
    sales_order_no TEXT,
    cost_center TEXT,
    source_warehouse_code TEXT,
    wip_warehouse_code TEXT,
    target_warehouse_code TEXT,
    scrap_warehouse_code TEXT,
    produced_qty REAL NOT NULL DEFAULT 0,
    accumulated_cost REAL NOT NULL DEFAULT 0,
    wip_balance REAL NOT NULL DEFAULT 0,
    variance_amount REAL NOT NULL DEFAULT 0,
    standard_variance REAL NOT NULL DEFAULT 0,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

INSERT INTO mfg_orders (id, company_id, doc_no, doc_date, item_code, item_name,
    qty, unit_code, warehouse_code, overhead_amount, standard_unit_cost,
    total_cost, unit_cost, status, notes, is_deleted, created_by, created_at,
    updated_by, updated_at)
SELECT id, company_id, doc_no, doc_date, item_code, item_name,
    qty, unit_code, warehouse_code, overhead_amount, standard_unit_cost,
    total_cost, unit_cost, status, notes, is_deleted, created_by, created_at,
    updated_by, updated_at
FROM mfg_orders_pre058;

CREATE TABLE mfg_order_lines (
    id TEXT PRIMARY KEY,
    order_id TEXT NOT NULL REFERENCES mfg_orders(id),
    line_no INTEGER NOT NULL DEFAULT 0,
    line_kind TEXT NOT NULL DEFAULT 'RAW',
    item_code TEXT NOT NULL,
    item_name TEXT,
    qty REAL NOT NULL CHECK (qty > 0),
    unit_code TEXT,
    unit_cost REAL NOT NULL DEFAULT 0 CHECK (unit_cost >= 0),
    standard_unit_cost REAL NOT NULL DEFAULT 0 CHECK (standard_unit_cost >= 0),
    amount REAL NOT NULL DEFAULT 0,
    consumed_qty REAL NOT NULL DEFAULT 0,
    produced_qty REAL NOT NULL DEFAULT 0,
    warehouse_code TEXT,
    cost_share_pct REAL NOT NULL DEFAULT 0 CHECK (cost_share_pct >= 0 AND cost_share_pct <= 100)
);

INSERT INTO mfg_order_lines (id, order_id, line_no, line_kind, item_code,
    item_name, qty, unit_code, unit_cost, standard_unit_cost, amount,
    consumed_qty, produced_qty, warehouse_code, cost_share_pct)
SELECT id, order_id, ROW_NUMBER() OVER (PARTITION BY order_id ORDER BY item_code),
    line_kind, item_code, item_name, qty, unit_code, unit_cost,
    standard_unit_cost, amount, 0, 0, NULL, 0
FROM mfg_order_lines_pre058;

DROP TABLE mfg_order_lines_pre058;
DROP TABLE mfg_orders_pre058;

CREATE UNIQUE INDEX IF NOT EXISTS ux_mfg_orders_company_docno
    ON mfg_orders(company_id, doc_no) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_mfg_order_lines_order
    ON mfg_order_lines(order_id);
CREATE INDEX IF NOT EXISTS ix_mfg_orders_company_status
    ON mfg_orders(company_id, status, doc_date) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_mfg_orders_company_bom
    ON mfg_orders(company_id, bom_id) WHERE bom_id IS NOT NULL AND is_deleted = 0;

-- ---- Production events (staged action audit trail) -----------------------------
CREATE TABLE IF NOT EXISTS mfg_production_events (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    order_id TEXT NOT NULL REFERENCES mfg_orders(id),
    event_type TEXT NOT NULL CHECK (event_type IN ('Confirm','Issue','Finish','Close','Cancel')),
    event_date TEXT NOT NULL,
    seq INTEGER NOT NULL DEFAULT 0,
    qty REAL NOT NULL DEFAULT 0,
    amount REAL NOT NULL DEFAULT 0,
    journal_doc_no TEXT,
    notes TEXT,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_mfg_production_events_order
    ON mfg_production_events(company_id, order_id, seq);

-- ---- Planning (MRP-lite) ---------------------------------------------------------
CREATE TABLE IF NOT EXISTS mfg_planning_runs (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    run_date TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'Completed',
    notes TEXT,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_mfg_planning_runs_company
    ON mfg_planning_runs(company_id, created_at);

CREATE TABLE IF NOT EXISTS mfg_planned_orders (
    id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL REFERENCES mfg_planning_runs(id),
    company_id TEXT NOT NULL,
    material_code TEXT NOT NULL,
    material_name TEXT,
    qty REAL NOT NULL CHECK (qty > 0),
    unit_code TEXT NOT NULL DEFAULT 'PCS',
    bom_id TEXT,
    due_date TEXT,
    source TEXT,
    status TEXT NOT NULL DEFAULT 'Planned'
        CHECK (status IN ('Planned','Converted','Cancelled')),
    converted_order_id TEXT,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_mfg_planned_orders_run
    ON mfg_planned_orders(company_id, run_id, status);

CREATE TABLE IF NOT EXISTS mfg_purchase_suggestions (
    id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL REFERENCES mfg_planning_runs(id),
    company_id TEXT NOT NULL,
    material_code TEXT NOT NULL,
    material_name TEXT,
    qty REAL NOT NULL CHECK (qty > 0),
    unit_code TEXT,
    source TEXT,
    status TEXT NOT NULL DEFAULT 'Suggested'
        CHECK (status IN ('Suggested','Converted','Cancelled')),
    converted_doc_id INTEGER,
    converted_doc_no TEXT,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_mfg_purchase_suggestions_run
    ON mfg_purchase_suggestions(company_id, run_id, status);

-- ---- Permissions (028/038/053-057 pattern; display_order continues at 366) -----
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('MFG.PLANNING.VIEW','Manufacturing Planning - View','MANUFACTURING','PLANNING','VIEW','Manufacturing Planning - View','Manufacturing Planning - View','View manufacturing planning runs and suggestions (Build 058)',366,0);
UPDATE permissions SET group_code='MANUFACTURING',feature_code='PLANNING',operation_code='VIEW',name_ar='Manufacturing Planning - View',name_en='Manufacturing Planning - View',display_order=366,is_sensitive=0 WHERE permission_code='MFG.PLANNING.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('MFG.PLANNING.RUN','Manufacturing Planning - Run','MANUFACTURING','PLANNING','RUN','Manufacturing Planning - Run','Manufacturing Planning - Run','Run MRP-lite planning (Build 058)',367,0);
UPDATE permissions SET group_code='MANUFACTURING',feature_code='PLANNING',operation_code='RUN',name_ar='Manufacturing Planning - Run',name_en='Manufacturing Planning - Run',display_order=367,is_sensitive=0 WHERE permission_code='MFG.PLANNING.RUN';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('MFG.PLANNING.CONVERT','Manufacturing Planning - Convert','MANUFACTURING','PLANNING','CONVERT','Manufacturing Planning - Convert','Manufacturing Planning - Convert','Convert planned orders and purchase suggestions into real documents (Build 058)',368,1);
UPDATE permissions SET group_code='MANUFACTURING',feature_code='PLANNING',operation_code='CONVERT',name_ar='Manufacturing Planning - Convert',name_en='Manufacturing Planning - Convert',display_order=368,is_sensitive=1 WHERE permission_code='MFG.PLANNING.CONVERT';

INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('MFG.PLANNING.VIEW','MFG.PLANNING.RUN','MFG.PLANNING.CONVERT');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('MFG.PLANNING.VIEW','MFG.PLANNING.RUN','MFG.PLANNING.CONVERT');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_VIEWER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('MFG.PLANNING.VIEW');
-- @shoutech-migration 131_mfg_template_sections.sql 693F65B2E9FBBB04C6F85FDD67EEEECBF0EC1C7583521DDF40CDD75295F1E477 693F65B2E9FBBB04C6F85FDD67EEEECBF0EC1C7583521DDF40CDD75295F1E477 693F65B2E9FBBB04C6F85FDD67EEEECBF0EC1C7583521DDF40CDD75295F1E477 693F65B2E9FBBB04C6F85FDD67EEEECBF0EC1C7583521DDF40CDD75295F1E477
-- 131 - Persist complete reusable manufacturing template sections.
-- Inputs remain in mfg_bom_lines. Expenses and all output products are stored
-- independently so template selection can reproduce the full recipe.
CREATE TABLE IF NOT EXISTS mfg_bom_expenses (
    id TEXT PRIMARY KEY,
    bom_id TEXT NOT NULL REFERENCES mfg_boms(id) ON DELETE CASCADE,
    line_no INTEGER NOT NULL,
    expense_code TEXT NOT NULL,
    description TEXT,
    amount NUMERIC NOT NULL DEFAULT 0 CHECK (amount >= 0),
    debit_account_code TEXT,
    credit_account_code TEXT,
    cost_center TEXT,
    UNIQUE (bom_id, line_no)
);
CREATE INDEX IF NOT EXISTS ix_mfg_bom_expenses_bom
    ON mfg_bom_expenses(bom_id, line_no);

CREATE TABLE IF NOT EXISTS mfg_bom_outputs (
    id TEXT PRIMARY KEY,
    bom_id TEXT NOT NULL REFERENCES mfg_boms(id) ON DELETE CASCADE,
    line_no INTEGER NOT NULL,
    material_code TEXT NOT NULL,
    material_name TEXT,
    output_kind TEXT NOT NULL DEFAULT 'MAIN'
        CHECK (output_kind IN ('MAIN','CO_PRODUCT','BY_PRODUCT','SCRAP')),
    qty NUMERIC NOT NULL CHECK (qty > 0),
    unit_code TEXT,
    warehouse_code TEXT,
    cost_share_pct NUMERIC NOT NULL DEFAULT 0
        CHECK (cost_share_pct >= 0 AND cost_share_pct <= 100),
    UNIQUE (bom_id, line_no)
);
CREATE INDEX IF NOT EXISTS ix_mfg_bom_outputs_bom
    ON mfg_bom_outputs(bom_id, line_no);
