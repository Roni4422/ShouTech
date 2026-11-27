-- Material groups, card sequences, and warehouses are DBA-owned schema.
CREATE TABLE IF NOT EXISTS material_groups (
    group_id INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL DEFAULT '',
    card_no INTEGER,
    group_code TEXT NOT NULL,
    group_name TEXT NOT NULL DEFAULT '',
    group_name_en TEXT NOT NULL DEFAULT '',
    parent_code TEXT,
    warehouse TEXT,
    notes TEXT NOT NULL DEFAULT '',
    general_specs TEXT NOT NULL DEFAULT '',
    image_path TEXT,
    is_sales_class INTEGER NOT NULL DEFAULT 0,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Active',
    created_by TEXT,
    created_at TEXT,
    updated_by TEXT,
    updated_at TEXT
);
-- @if-column-missing material_groups.deleted_at
ALTER TABLE material_groups ADD COLUMN deleted_at TEXT;
-- @if-column-missing material_groups.deleted_by
ALTER TABLE material_groups ADD COLUMN deleted_by TEXT;
-- @if-column-missing material_groups.card_no
ALTER TABLE material_groups ADD COLUMN card_no INTEGER;
-- @if-column-missing material_groups.general_specs
ALTER TABLE material_groups ADD COLUMN general_specs TEXT NOT NULL DEFAULT '';
-- @if-column-missing material_groups.image_path
ALTER TABLE material_groups ADD COLUMN image_path TEXT;
-- @if-column-missing material_groups.parent_code
ALTER TABLE material_groups ADD COLUMN parent_code TEXT;
-- @if-column-missing material_groups.warehouse
ALTER TABLE material_groups ADD COLUMN warehouse TEXT;
-- @if-column-missing material_groups.notes
ALTER TABLE material_groups ADD COLUMN notes TEXT NOT NULL DEFAULT '';
-- @if-column-missing material_groups.group_name_en
ALTER TABLE material_groups ADD COLUMN group_name_en TEXT NOT NULL DEFAULT '';
-- @if-column-missing material_groups.status
ALTER TABLE material_groups ADD COLUMN status TEXT NOT NULL DEFAULT 'Active';
-- @if-column-missing material_groups.is_sales_class
ALTER TABLE material_groups ADD COLUMN is_sales_class INTEGER NOT NULL DEFAULT 0;
-- @if-column-missing material_groups.is_deleted
ALTER TABLE material_groups ADD COLUMN is_deleted INTEGER NOT NULL DEFAULT 0;
-- @if-column-missing material_groups.created_by
ALTER TABLE material_groups ADD COLUMN created_by TEXT;
-- @if-column-missing material_groups.created_at
ALTER TABLE material_groups ADD COLUMN created_at TEXT;
-- @if-column-missing material_groups.updated_by
ALTER TABLE material_groups ADD COLUMN updated_by TEXT;
-- @if-column-missing material_groups.updated_at
ALTER TABLE material_groups ADD COLUMN updated_at TEXT;
-- @if-column-missing material_groups.deleted_at
ALTER TABLE material_groups ADD COLUMN deleted_at TEXT;
-- @if-column-missing material_groups.deleted_by
ALTER TABLE material_groups ADD COLUMN deleted_by TEXT;
CREATE TABLE IF NOT EXISTS material_group_sequences (
    company_id TEXT PRIMARY KEY,
    last_card_no INTEGER NOT NULL DEFAULT 0
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_material_groups_company_card
    ON material_groups(company_id, card_no);
CREATE TABLE IF NOT EXISTS warehouses (
    warehouse_id INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL DEFAULT '',
    card_no INTEGER NOT NULL DEFAULT 0,
    warehouse_code INTEGER NOT NULL DEFAULT 0,
    warehouse_name_ar TEXT NOT NULL DEFAULT '',
    warehouse_name_en TEXT,
    parent_code INTEGER,
    address_line1 TEXT,
    city TEXT,
    region TEXT,
    country TEXT,
    phone TEXT,
    aisle TEXT,
    rack TEXT,
    shelf TEXT,
    notes TEXT,
    is_default INTEGER NOT NULL DEFAULT 0,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT,
    updated_at TEXT
);
-- @if-column-missing warehouses.deleted_at
ALTER TABLE warehouses ADD COLUMN deleted_at TEXT;
-- @if-column-missing warehouses.deleted_by
ALTER TABLE warehouses ADD COLUMN deleted_by TEXT;
-- @if-column-missing warehouses.card_no
ALTER TABLE warehouses ADD COLUMN card_no INTEGER NOT NULL DEFAULT 0;
-- @if-column-missing warehouses.warehouse_code
ALTER TABLE warehouses ADD COLUMN warehouse_code INTEGER NOT NULL DEFAULT 0;
-- @if-column-missing warehouses.warehouse_name_ar
ALTER TABLE warehouses ADD COLUMN warehouse_name_ar TEXT NOT NULL DEFAULT '';
-- @if-column-missing warehouses.warehouse_name_en
ALTER TABLE warehouses ADD COLUMN warehouse_name_en TEXT;
-- @if-column-missing warehouses.parent_code
ALTER TABLE warehouses ADD COLUMN parent_code INTEGER;
-- @if-column-missing warehouses.address_line1
ALTER TABLE warehouses ADD COLUMN address_line1 TEXT;
-- @if-column-missing warehouses.city
ALTER TABLE warehouses ADD COLUMN city TEXT;
-- @if-column-missing warehouses.region
ALTER TABLE warehouses ADD COLUMN region TEXT;
-- @if-column-missing warehouses.country
ALTER TABLE warehouses ADD COLUMN country TEXT;
-- @if-column-missing warehouses.phone
ALTER TABLE warehouses ADD COLUMN phone TEXT;
-- @if-column-missing warehouses.aisle
ALTER TABLE warehouses ADD COLUMN aisle TEXT;
-- @if-column-missing warehouses.rack
ALTER TABLE warehouses ADD COLUMN rack TEXT;
-- @if-column-missing warehouses.shelf
ALTER TABLE warehouses ADD COLUMN shelf TEXT;
-- @if-column-missing warehouses.notes
ALTER TABLE warehouses ADD COLUMN notes TEXT;
-- @if-column-missing warehouses.is_default
ALTER TABLE warehouses ADD COLUMN is_default INTEGER NOT NULL DEFAULT 0;
-- @if-column-missing warehouses.is_active
ALTER TABLE warehouses ADD COLUMN is_active INTEGER NOT NULL DEFAULT 1;
-- @if-column-missing warehouses.is_deleted
ALTER TABLE warehouses ADD COLUMN is_deleted INTEGER NOT NULL DEFAULT 0;
-- @if-column-missing warehouses.created_at
ALTER TABLE warehouses ADD COLUMN created_at TEXT;
-- @if-column-missing warehouses.updated_at
ALTER TABLE warehouses ADD COLUMN updated_at TEXT;
-- @if-column-missing warehouses.deleted_at
ALTER TABLE warehouses ADD COLUMN deleted_at TEXT;
-- @if-column-missing warehouses.deleted_by
ALTER TABLE warehouses ADD COLUMN deleted_by TEXT;
CREATE UNIQUE INDEX IF NOT EXISTS ux_warehouses_company_cardno
    ON warehouses(company_id, card_no) WHERE card_no > 0 AND is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_warehouses_company
    ON warehouses(company_id, is_active) WHERE is_deleted = 0;
