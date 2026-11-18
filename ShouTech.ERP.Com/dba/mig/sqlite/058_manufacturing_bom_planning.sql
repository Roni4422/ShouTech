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
