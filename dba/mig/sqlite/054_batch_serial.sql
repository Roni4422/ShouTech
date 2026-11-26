-- ============================================================================
-- 054 - Batch / serial tracking (SQLite)
--
-- Phase B design section 2 (migration 054): the instance layer behind the
-- material-card tracking flags that have existed since 013 and were honored
-- by nothing (materials.has_batch / has_lot / has_serial / has_expiry /
-- shelf_life_days / alert_before_expiry_days). has_batch and has_lot are ONE
-- concept (batch), matching Odoo's unification and ERPNext practice.
--
--   * stock_batches: one row per (company, material, batch_no) - the batch
--     master (ERPNext Batch / Odoo stock.lot pattern): manufacturing and
--     expiry dates, the document that created it, the supplier's own batch
--     number kept alongside the internal one (owner decision B4), status.
--   * stock_batch_balances: on-hand per (company, warehouse, batch) -
--     mirrors material_stock_balances so batch stock reports are O(1);
--     maintained by the same posting code that writes allocations.
--   * stock_serials: one row per physical unit (Phenix-style duplicate
--     prevention enforced by the UNIQUE constraint, not the UI): status
--     lifecycle InStock / Delivered / Returned / Consumed / Scrapped,
--     current warehouse, the party holding it, warranty expiry.
--   * stock_movement_allocations: the Serial-and-Batch-Bundle link
--     (ERPNext pattern): one row per batch (signed qty) or per serial
--     (qty = +/-1) per movement, so every movement is traceable to its
--     batches/serials and every batch/serial to its movements.
--   * materials.removal_strategy: FIFO / FEFO picking order (Odoo removal
--     strategy). FEFO auto-applies to has_expiry materials at picking time.
--
-- EXISTING STOCK: no batches or serials are fabricated for pre-054
-- balances (owner decision B3): on-hand quantities of tracked materials
-- gain batch/serial identity through opening / count documents whose
-- postings write allocations. Materials without tracking flags post
-- exactly as before - the pre-054 engine behavior is the regression
-- baseline and this migration changes no existing row's meaning.
--
-- NUMBERING NOTE: like 053, this file sorts before the already-shipped
-- 059-064 files; the migrator applies pending migrations in filename
-- order and skips by checksum. 054 depends only on 013-era objects
-- (materials, warehouses) plus roles/permissions (002-004), never on
-- 059-064 objects, so it applies cleanly on a database that already
-- ran them.
--
-- Idempotent: table/index creation is IF NOT EXISTS, the column add is
-- guarded by the migrator directive, permission seeds are INSERT OR
-- IGNORE + UPDATE, and the checksum ledger skips a re-run.
-- ============================================================================

-- ---- Picking strategy on the material card --------------------------------
-- @if-column-missing materials.removal_strategy
ALTER TABLE materials ADD COLUMN removal_strategy TEXT NOT NULL DEFAULT 'FIFO';

-- ---- Batch master -----------------------------------------------------------
CREATE TABLE IF NOT EXISTS stock_batches (
    batch_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    material_id INTEGER NOT NULL,
    batch_no TEXT NOT NULL,
    supplier_batch_no TEXT,
    mfg_date TEXT,
    expiry_date TEXT,
    source_doc_kind TEXT NOT NULL DEFAULT '',
    source_doc_no TEXT NOT NULL DEFAULT '',
    supplier_party_id INTEGER,
    status TEXT NOT NULL DEFAULT 'Active',
    notes TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT,
    created_by TEXT,
    updated_by TEXT,
    UNIQUE(company_id, material_id, batch_no)
);
CREATE INDEX IF NOT EXISTS ix_stock_batches_material
    ON stock_batches(company_id, material_id, is_deleted);
CREATE INDEX IF NOT EXISTS ix_stock_batches_expiry
    ON stock_batches(company_id, expiry_date, status)
    WHERE is_deleted = 0;

-- ---- Batch balances (per warehouse) -----------------------------------------
CREATE TABLE IF NOT EXISTS stock_batch_balances (
    company_id TEXT NOT NULL,
    warehouse_id INTEGER NOT NULL,
    batch_id INTEGER NOT NULL,
    qty_on_hand NUMERIC NOT NULL DEFAULT 0,
    reserved_qty NUMERIC NOT NULL DEFAULT 0,
    last_updated TEXT NOT NULL DEFAULT (datetime('now')),
    PRIMARY KEY (company_id, warehouse_id, batch_id)
);
CREATE INDEX IF NOT EXISTS ix_stock_batch_balances_batch
    ON stock_batch_balances(company_id, batch_id);

-- ---- Serial master ------------------------------------------------------------
CREATE TABLE IF NOT EXISTS stock_serials (
    serial_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    material_id INTEGER NOT NULL,
    serial_no TEXT NOT NULL,
    batch_id INTEGER,
    status TEXT NOT NULL DEFAULT 'InStock',
    current_warehouse_id INTEGER,
    party_id INTEGER,
    warranty_expiry TEXT,
    source_doc_kind TEXT NOT NULL DEFAULT '',
    source_doc_no TEXT NOT NULL DEFAULT '',
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT,
    created_by TEXT,
    updated_by TEXT,
    UNIQUE(company_id, material_id, serial_no)
);
CREATE INDEX IF NOT EXISTS ix_stock_serials_material_status
    ON stock_serials(company_id, material_id, status, is_deleted);
CREATE INDEX IF NOT EXISTS ix_stock_serials_batch
    ON stock_serials(company_id, batch_id);

-- ---- Movement allocations (the bundle link) -----------------------------------
CREATE TABLE IF NOT EXISTS stock_movement_allocations (
    allocation_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    stock_movement_id INTEGER NOT NULL,
    batch_id INTEGER,
    serial_id INTEGER,
    qty NUMERIC NOT NULL DEFAULT 0,
    expiry_date TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_stock_movement_allocations_movement
    ON stock_movement_allocations(stock_movement_id);
CREATE INDEX IF NOT EXISTS ix_stock_movement_allocations_batch
    ON stock_movement_allocations(batch_id, stock_movement_id);
CREATE INDEX IF NOT EXISTS ix_stock_movement_allocations_serial
    ON stock_movement_allocations(serial_id, stock_movement_id);

-- ---- Permissions (028/038/053 pattern) -----------------------------------------
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.BATCH.VIEW','Inventory Batch - View','INVENTORY','BATCH','VIEW','Inventory Batch - View','Inventory Batch - View','View batches, batch balances and batch traceability (Phase B 054)',342,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='BATCH',operation_code='VIEW',name_ar='Inventory Batch - View',name_en='Inventory Batch - View',display_order=342,is_sensitive=0 WHERE permission_code='INV.BATCH.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.BATCH.MANAGE','Inventory Batch - Manage','INVENTORY','BATCH','MANAGE','Inventory Batch - Manage','Inventory Batch - Manage','Create and maintain batches (Phase B 054)',343,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='BATCH',operation_code='MANAGE',name_ar='Inventory Batch - Manage',name_en='Inventory Batch - Manage',display_order=343,is_sensitive=0 WHERE permission_code='INV.BATCH.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.SERIAL.VIEW','Inventory Serial - View','INVENTORY','SERIAL','VIEW','Inventory Serial - View','Inventory Serial - View','View serial numbers and serial traceability (Phase B 054)',344,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='SERIAL',operation_code='VIEW',name_ar='Inventory Serial - View',name_en='Inventory Serial - View',display_order=344,is_sensitive=0 WHERE permission_code='INV.SERIAL.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.BATCH.EXPIREDOVERRIDE','Inventory Batch - Issue Expired Batch','INVENTORY','BATCH','EXPIREDOVERRIDE','Inventory Batch - Issue Expired Batch','Inventory Batch - Issue Expired Batch','Issue an expired batch anyway; the override is recorded on the movement allocations (Phase B 054, decision B1)',345,1);
UPDATE permissions SET group_code='INVENTORY',feature_code='BATCH',operation_code='EXPIREDOVERRIDE',name_ar='Inventory Batch - Issue Expired Batch',name_en='Inventory Batch - Issue Expired Batch',display_order=345,is_sensitive=1 WHERE permission_code='INV.BATCH.EXPIREDOVERRIDE';

INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('INV.BATCH.VIEW','INV.BATCH.MANAGE','INV.SERIAL.VIEW','INV.BATCH.EXPIREDOVERRIDE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('INV.BATCH.VIEW','INV.BATCH.MANAGE','INV.SERIAL.VIEW');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_VIEWER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('INV.BATCH.VIEW','INV.SERIAL.VIEW');
