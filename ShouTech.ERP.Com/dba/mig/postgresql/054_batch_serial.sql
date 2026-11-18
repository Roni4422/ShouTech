-- ============================================================================
-- 054 - Batch / serial tracking (PostgreSQL)
-- PostgreSQL counterpart of sqlite/054_batch_serial.sql (Phase B design
-- section 2). See the SQLite file for the design notes, the existing-stock
-- rule (decision B3: no fabricated batches) and the numbering note (054
-- applies cleanly after 059-064).
-- ============================================================================

ALTER TABLE materials ADD COLUMN IF NOT EXISTS removal_strategy TEXT NOT NULL DEFAULT 'FIFO';

CREATE TABLE IF NOT EXISTS stock_batches (
    batch_id BIGSERIAL PRIMARY KEY,
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
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT,
    UNIQUE(company_id, material_id, batch_no)
);
CREATE INDEX IF NOT EXISTS ix_stock_batches_material
    ON stock_batches(company_id, material_id, is_deleted);
CREATE INDEX IF NOT EXISTS ix_stock_batches_expiry
    ON stock_batches(company_id, expiry_date, status)
    WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS stock_batch_balances (
    company_id TEXT NOT NULL,
    warehouse_id INTEGER NOT NULL,
    batch_id INTEGER NOT NULL,
    qty_on_hand NUMERIC NOT NULL DEFAULT 0,
    reserved_qty NUMERIC NOT NULL DEFAULT 0,
    last_updated TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (company_id, warehouse_id, batch_id)
);
CREATE INDEX IF NOT EXISTS ix_stock_batch_balances_batch
    ON stock_batch_balances(company_id, batch_id);

CREATE TABLE IF NOT EXISTS stock_serials (
    serial_id BIGSERIAL PRIMARY KEY,
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
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT,
    UNIQUE(company_id, material_id, serial_no)
);
CREATE INDEX IF NOT EXISTS ix_stock_serials_material_status
    ON stock_serials(company_id, material_id, status, is_deleted);
CREATE INDEX IF NOT EXISTS ix_stock_serials_batch
    ON stock_serials(company_id, batch_id);

CREATE TABLE IF NOT EXISTS stock_movement_allocations (
    allocation_id BIGSERIAL PRIMARY KEY,
    stock_movement_id INTEGER NOT NULL,
    batch_id INTEGER,
    serial_id INTEGER,
    qty NUMERIC NOT NULL DEFAULT 0,
    expiry_date TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_stock_movement_allocations_movement
    ON stock_movement_allocations(stock_movement_id);
CREATE INDEX IF NOT EXISTS ix_stock_movement_allocations_batch
    ON stock_movement_allocations(batch_id, stock_movement_id);
CREATE INDEX IF NOT EXISTS ix_stock_movement_allocations_serial
    ON stock_movement_allocations(serial_id, stock_movement_id);

INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.BATCH.VIEW','Inventory Batch - View','INVENTORY','BATCH','VIEW','Inventory Batch - View','Inventory Batch - View','View batches, batch balances and batch traceability (Phase B 054)',342,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.BATCH.MANAGE','Inventory Batch - Manage','INVENTORY','BATCH','MANAGE','Inventory Batch - Manage','Inventory Batch - Manage','Create and maintain batches (Phase B 054)',343,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.SERIAL.VIEW','Inventory Serial - View','INVENTORY','SERIAL','VIEW','Inventory Serial - View','Inventory Serial - View','View serial numbers and serial traceability (Phase B 054)',344,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.BATCH.EXPIREDOVERRIDE','Inventory Batch - Issue Expired Batch','INVENTORY','BATCH','EXPIREDOVERRIDE','Inventory Batch - Issue Expired Batch','Inventory Batch - Issue Expired Batch','Issue an expired batch anyway; the override is recorded on the movement allocations (Phase B 054, decision B1)',345,true) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('INV.BATCH.VIEW','INV.BATCH.MANAGE','INV.SERIAL.VIEW','INV.BATCH.EXPIREDOVERRIDE') ON CONFLICT DO NOTHING;
INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('INV.BATCH.VIEW','INV.BATCH.MANAGE','INV.SERIAL.VIEW') ON CONFLICT DO NOTHING;
INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_VIEWER' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('INV.BATCH.VIEW','INV.SERIAL.VIEW') ON CONFLICT DO NOTHING;
