-- @shoutech-migration 007_inventory_indexes.sql 877ABFFA31A9D20321C043E8BAB1407FF9549CAF724E0EBDA60637E44CDEDBC7 877ABFFA31A9D20321C043E8BAB1407FF9549CAF724E0EBDA60637E44CDEDBC7
-- Query-performance indexes for the shared inventory ledger.
-- This migration is additive and preserves all existing business rows.
CREATE INDEX IF NOT EXISTS ix_products_company_name
    ON products(company_id, product_code, product_id);

CREATE INDEX IF NOT EXISTS ix_products_company_category
    ON products(company_id, category_id, is_deleted);

CREATE INDEX IF NOT EXISTS ix_stock_movements_company_product_date
    ON stock_movements(company_id, product_id, movement_date, movement_id);

CREATE INDEX IF NOT EXISTS ix_stock_movements_company_type_date
    ON stock_movements(company_id, movement_type, movement_date);

CREATE INDEX IF NOT EXISTS ix_stock_balances_company_warehouse_active
    ON stock_balances(company_id, warehouse_id, is_deleted, product_id);

-- Standardize inventory quantities to six decimal places for fractional units.
ALTER TABLE stock_balances
    ALTER COLUMN available_qty TYPE numeric(18,6) USING available_qty::numeric(18,6);

ALTER TABLE stock_movements
    ALTER COLUMN quantity TYPE numeric(18,6) USING quantity::numeric(18,6);
-- @shoutech-migration 015_inventory_schema.sql 9970822F457732B437AB82E89AA3C3D65C816CFC03242006161B3F279DE76C16 9970822F457732B437AB82E89AA3C3D65C816CFC03242006161B3F279DE76C16
-- Inventory schema is owned by DBA migrations. Service code must not create tables.
        CREATE TABLE IF NOT EXISTS materials (
            material_id BIGSERIAL PRIMARY KEY,
            company_id TEXT NOT NULL DEFAULT '',
            code TEXT NOT NULL,
            name TEXT NOT NULL DEFAULT '',
            short_name TEXT NOT NULL DEFAULT '',
            search_name TEXT NOT NULL DEFAULT '',
            alt_code TEXT,
            old_code TEXT,
            supplier_code TEXT,
            customer_code TEXT,
            manufacturer_code TEXT,
            hs_code TEXT,
            gtin TEXT,
            sku_external TEXT,
            pos_name TEXT NOT NULL DEFAULT '',
            group_code TEXT,
            category TEXT,
            sub_category TEXT,
            brand TEXT,
            model TEXT,
            color TEXT,
            size_spec TEXT,
            grade TEXT,
            class1 TEXT,
            class2 TEXT,
            class3 TEXT,
            class4 TEXT,
            class5 TEXT,
            product_line TEXT,
            abc_class TEXT,
            season TEXT,
            item_type TEXT NOT NULL DEFAULT 'Stock',
            status TEXT NOT NULL DEFAULT 'Active',
            lifecycle_status TEXT NOT NULL DEFAULT 'Active',
            make_or_buy TEXT NOT NULL DEFAULT 'Buy',
            stock_stage TEXT NOT NULL DEFAULT 'Finished',
            is_stocked BOOLEAN NOT NULL DEFAULT TRUE,
            is_purchased BOOLEAN NOT NULL DEFAULT TRUE,
            is_sold BOOLEAN NOT NULL DEFAULT TRUE,
            is_produced BOOLEAN NOT NULL DEFAULT FALSE,
            is_service BOOLEAN NOT NULL DEFAULT FALSE,
            weight_net NUMERIC NOT NULL DEFAULT 0,
            weight_gross NUMERIC NOT NULL DEFAULT 0,
            weight_unit TEXT,
            volume NUMERIC NOT NULL DEFAULT 0,
            volume_unit TEXT,
            length NUMERIC NOT NULL DEFAULT 0,
            width NUMERIC NOT NULL DEFAULT 0,
            height NUMERIC NOT NULL DEFAULT 0,
            dim_unit TEXT,
            area NUMERIC NOT NULL DEFAULT 0,
            area_unit TEXT,
            pack_qty NUMERIC NOT NULL DEFAULT 0,
            units_per_parcel NUMERIC NOT NULL DEFAULT 0,
            origin_country TEXT,
            manufacturer TEXT,
            brand_owner TEXT,
            source TEXT,
            warranty_months INTEGER NOT NULL DEFAULT 0,
            catalogue_ref TEXT,
            web_url TEXT,
            tags TEXT NOT NULL DEFAULT '',
            unit1 TEXT,
            unit1_factor NUMERIC NOT NULL DEFAULT 1,
            unit1_barcode TEXT,
            unit2 TEXT,
            unit2_factor NUMERIC NOT NULL DEFAULT 1,
            unit2_barcode TEXT,
            unit3 TEXT,
            unit3_factor NUMERIC NOT NULL DEFAULT 1,
            unit3_barcode TEXT,
            unit4 TEXT,
            unit4_factor NUMERIC NOT NULL DEFAULT 1,
            unit4_barcode TEXT,
            unit5 TEXT,
            unit5_factor NUMERIC NOT NULL DEFAULT 1,
            unit5_barcode TEXT,
            default_unit TEXT,
            purchase_unit TEXT,
            sales_unit TEXT,
            cost_method TEXT NOT NULL DEFAULT 'WeightedAvg',
            cost NUMERIC NOT NULL DEFAULT 0,
            standard_cost NUMERIC NOT NULL DEFAULT 0,
            last_purchase_cost NUMERIC NOT NULL DEFAULT 0,
            avg_cost NUMERIC NOT NULL DEFAULT 0,
            cost_currency TEXT NOT NULL DEFAULT '',
            overhead_pct NUMERIC NOT NULL DEFAULT 0,
            landed_cost NUMERIC NOT NULL DEFAULT 0,
            last_purchase_date TEXT,
            price_retail NUMERIC NOT NULL DEFAULT 0,
            price_wholesale NUMERIC NOT NULL DEFAULT 0,
            price_special NUMERIC NOT NULL DEFAULT 0,
            price_min NUMERIC NOT NULL DEFAULT 0,
            price_list NUMERIC NOT NULL DEFAULT 0,
            price_export NUMERIC NOT NULL DEFAULT 0,
            price_distributor NUMERIC NOT NULL DEFAULT 0,
            price_online NUMERIC NOT NULL DEFAULT 0,
            price_currency TEXT NOT NULL DEFAULT '',
            margin_target_pct NUMERIC NOT NULL DEFAULT 0,
            discount_max_pct NUMERIC NOT NULL DEFAULT 0,
            allow_invoice_disc BOOLEAN NOT NULL DEFAULT TRUE,
            price_includes_tax BOOLEAN NOT NULL DEFAULT FALSE,
            qty_on_hand NUMERIC NOT NULL DEFAULT 0,
            min_qty NUMERIC NOT NULL DEFAULT 0,
            max_qty NUMERIC NOT NULL DEFAULT 0,
            reorder_qty NUMERIC NOT NULL DEFAULT 0,
            safety_stock NUMERIC NOT NULL DEFAULT 0,
            default_warehouse TEXT,
            shelf_bin TEXT,
            lead_time_days INTEGER NOT NULL DEFAULT 0,
            lot_size NUMERIC NOT NULL DEFAULT 0,
            order_multiple NUMERIC NOT NULL DEFAULT 0,
            economic_order_qty NUMERIC NOT NULL DEFAULT 0,
            replenishment_method TEXT NOT NULL DEFAULT 'Manual',
            has_expiry BOOLEAN NOT NULL DEFAULT FALSE,
            shelf_life_days INTEGER NOT NULL DEFAULT 0,
            alert_before_expiry_days INTEGER NOT NULL DEFAULT 0,
            has_serial BOOLEAN NOT NULL DEFAULT FALSE,
            has_batch BOOLEAN NOT NULL DEFAULT FALSE,
            has_lot BOOLEAN NOT NULL DEFAULT FALSE,
            track_location BOOLEAN NOT NULL DEFAULT FALSE,
            quality_check_required BOOLEAN NOT NULL DEFAULT FALSE,
            is_taxable BOOLEAN NOT NULL DEFAULT FALSE,
            tax_rate NUMERIC NOT NULL DEFAULT 0,
            tax_code TEXT,
            tax_account TEXT,
            tax_type TEXT,
            is_zero_rated BOOLEAN NOT NULL DEFAULT FALSE,
            acc_inventory TEXT,
            acc_sales TEXT,
            acc_sales_return TEXT,
            acc_cogs TEXT,
            acc_purchase TEXT,
            acc_purchase_return TEXT,
            acc_expense TEXT,
            acc_wip TEXT,
            allow_sale BOOLEAN NOT NULL DEFAULT TRUE,
            allow_purchase BOOLEAN NOT NULL DEFAULT TRUE,
            allow_return BOOLEAN NOT NULL DEFAULT TRUE,
            allow_negative BOOLEAN NOT NULL DEFAULT FALSE,
            commission_pct NUMERIC NOT NULL DEFAULT 0,
            bonus_qty NUMERIC NOT NULL DEFAULT 0,
            bonus_item_code TEXT,
            min_sale_qty NUMERIC NOT NULL DEFAULT 0,
            max_sale_qty NUMERIC NOT NULL DEFAULT 0,
            sales_blocked BOOLEAN NOT NULL DEFAULT FALSE,
            image_path TEXT,
            image_path2 TEXT,
            attachment_path TEXT,
            description TEXT NOT NULL DEFAULT '',
            notes TEXT NOT NULL DEFAULT '',
            internal_notes TEXT NOT NULL DEFAULT '',
            spec_sheet TEXT NOT NULL DEFAULT '',
            label_text TEXT NOT NULL DEFAULT '',
            units_json TEXT NOT NULL DEFAULT '[]',
            prices_json TEXT NOT NULL DEFAULT '[]',
            attributes_json TEXT NOT NULL DEFAULT '{}',
            is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
            created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
            updated_at TIMESTAMPTZ,
            created_by TEXT,
            updated_by TEXT,
            UNIQUE(company_id, code)
        );
        CREATE INDEX IF NOT EXISTS ix_materials_code ON materials(company_id, code);
        CREATE INDEX IF NOT EXISTS ix_materials_group ON materials(company_id, group_code);
        CREATE TABLE IF NOT EXISTS material_attributes (
            attr_id BIGSERIAL PRIMARY KEY, company_id TEXT NOT NULL DEFAULT '',
            material_id BIGINT NOT NULL, attr_key TEXT NOT NULL, attr_value TEXT NOT NULL DEFAULT '',
            attr_group TEXT NOT NULL DEFAULT '', sort_order INTEGER NOT NULL DEFAULT 0,
            UNIQUE(company_id, material_id, attr_key));
        CREATE TABLE IF NOT EXISTS material_extra_barcodes (
            barcode_id BIGSERIAL PRIMARY KEY, company_id TEXT NOT NULL DEFAULT '',
            material_id BIGINT NOT NULL, barcode TEXT NOT NULL, barcode_type TEXT NOT NULL DEFAULT 'EAN',
            unit_name TEXT, UNIQUE(company_id, barcode));
        CREATE TABLE IF NOT EXISTS material_price_list (
            price_id BIGSERIAL PRIMARY KEY, company_id TEXT NOT NULL DEFAULT '',
            material_id BIGINT NOT NULL, price_level TEXT NOT NULL, price NUMERIC NOT NULL DEFAULT 0,
            currency TEXT NOT NULL DEFAULT '', unit_name TEXT, valid_from TEXT, valid_to TEXT);
        CREATE TABLE IF NOT EXISTS material_suppliers (
            id BIGSERIAL PRIMARY KEY, company_id TEXT NOT NULL DEFAULT '',
            material_id BIGINT NOT NULL, supplier_code TEXT NOT NULL, supplier_item_code TEXT,
            lead_time_days INTEGER NOT NULL DEFAULT 0, min_order_qty NUMERIC NOT NULL DEFAULT 0,
            is_preferred INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS material_stock_balances (
            stock_balance_id BIGSERIAL PRIMARY KEY,
            company_id TEXT NOT NULL, warehouse_id BIGINT NOT NULL, material_id BIGINT NOT NULL,
            available_qty NUMERIC(18,4) NOT NULL DEFAULT 0, reserved_qty NUMERIC(18,4) NOT NULL DEFAULT 0,
            average_cost NUMERIC(18,6) NOT NULL DEFAULT 0, last_cost NUMERIC(18,6) NOT NULL DEFAULT 0,
            last_updated TIMESTAMPTZ NOT NULL DEFAULT NOW(), is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
            UNIQUE(company_id, warehouse_id, material_id));
        CREATE INDEX IF NOT EXISTS ix_material_stock_balances_wh_mat
            ON material_stock_balances(company_id, warehouse_id, material_id)
            WHERE is_deleted = FALSE;
        CREATE TABLE IF NOT EXISTS material_stock_movements (
            stock_movement_id BIGSERIAL PRIMARY KEY,
            company_id TEXT NOT NULL, warehouse_id BIGINT NOT NULL, material_id BIGINT NOT NULL,
            movement_date TIMESTAMPTZ NOT NULL, movement_type VARCHAR(30) NOT NULL, document_no TEXT NOT NULL,
            qty_in NUMERIC(18,4) NOT NULL DEFAULT 0, qty_out NUMERIC(18,4) NOT NULL DEFAULT 0,
            unit_cost NUMERIC(18,6) NOT NULL DEFAULT 0, note TEXT, is_deleted BOOLEAN NOT NULL DEFAULT FALSE);
        CREATE INDEX IF NOT EXISTS ix_material_stock_movements_wh_mat_date
            ON material_stock_movements(company_id, warehouse_id, material_id, movement_date);
ALTER TABLE materials ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ;
ALTER TABLE materials ADD COLUMN IF NOT EXISTS deleted_by TEXT;
-- @shoutech-migration 016_material_groups_warehouses.sql F145320631A7841EAF8CC80878CEF93BCBC031C2896ED0ED77E3D342AF82CDBC F145320631A7841EAF8CC80878CEF93BCBC031C2896ED0ED77E3D342AF82CDBC
-- Material groups, card sequences, and warehouses are DBA-owned schema.
CREATE TABLE IF NOT EXISTS material_groups (
    group_id BIGSERIAL PRIMARY KEY,
    company_id TEXT NOT NULL DEFAULT '',
    card_no BIGINT,
    group_code TEXT NOT NULL,
    group_name TEXT NOT NULL DEFAULT '',
    group_name_en TEXT NOT NULL DEFAULT '',
    parent_code BIGINT,
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
ALTER TABLE material_groups ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ;
ALTER TABLE material_groups ADD COLUMN IF NOT EXISTS deleted_by TEXT;
ALTER TABLE material_groups ADD COLUMN IF NOT EXISTS card_no BIGINT;
ALTER TABLE material_groups ADD COLUMN IF NOT EXISTS general_specs TEXT NOT NULL DEFAULT '';
ALTER TABLE material_groups ADD COLUMN IF NOT EXISTS image_path TEXT;
ALTER TABLE material_groups ADD COLUMN IF NOT EXISTS parent_code TEXT;
ALTER TABLE material_groups ADD COLUMN IF NOT EXISTS warehouse TEXT;
ALTER TABLE material_groups ADD COLUMN IF NOT EXISTS notes TEXT NOT NULL DEFAULT '';
ALTER TABLE material_groups ADD COLUMN IF NOT EXISTS group_name_en TEXT NOT NULL DEFAULT '';
ALTER TABLE material_groups ADD COLUMN IF NOT EXISTS status TEXT NOT NULL DEFAULT 'Active';
ALTER TABLE material_groups ADD COLUMN IF NOT EXISTS is_sales_class INTEGER NOT NULL DEFAULT 0;
ALTER TABLE material_groups ADD COLUMN IF NOT EXISTS is_deleted INTEGER NOT NULL DEFAULT 0;
ALTER TABLE material_groups ADD COLUMN IF NOT EXISTS created_by TEXT;
ALTER TABLE material_groups ADD COLUMN IF NOT EXISTS created_at TEXT;
ALTER TABLE material_groups ADD COLUMN IF NOT EXISTS updated_by TEXT;
ALTER TABLE material_groups ADD COLUMN IF NOT EXISTS updated_at TEXT;
ALTER TABLE material_groups ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ;
ALTER TABLE material_groups ADD COLUMN IF NOT EXISTS deleted_by TEXT;
CREATE TABLE IF NOT EXISTS material_group_sequences (
    company_id TEXT PRIMARY KEY,
    last_card_no BIGINT NOT NULL DEFAULT 0
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_material_groups_company_card
    ON material_groups(company_id, card_no);
CREATE TABLE IF NOT EXISTS warehouses (
    warehouse_id BIGSERIAL PRIMARY KEY,
    company_id TEXT NOT NULL DEFAULT '',
    card_no BIGINT NOT NULL DEFAULT 0,
    warehouse_code BIGINT NOT NULL DEFAULT 0,
    warehouse_name_ar TEXT NOT NULL DEFAULT '',
    warehouse_name_en TEXT,
    parent_code BIGINT,
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
ALTER TABLE warehouses ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ;
ALTER TABLE warehouses ADD COLUMN IF NOT EXISTS deleted_by TEXT;
ALTER TABLE warehouses ADD COLUMN IF NOT EXISTS card_no BIGINT NOT NULL DEFAULT 0;
ALTER TABLE warehouses ADD COLUMN IF NOT EXISTS warehouse_code BIGINT NOT NULL DEFAULT 0;
ALTER TABLE warehouses ADD COLUMN IF NOT EXISTS warehouse_name_ar TEXT NOT NULL DEFAULT '';
ALTER TABLE warehouses ADD COLUMN IF NOT EXISTS warehouse_name_en TEXT;
ALTER TABLE warehouses ADD COLUMN IF NOT EXISTS parent_code BIGINT;
ALTER TABLE warehouses ADD COLUMN IF NOT EXISTS address_line1 TEXT;
ALTER TABLE warehouses ADD COLUMN IF NOT EXISTS city TEXT;
ALTER TABLE warehouses ADD COLUMN IF NOT EXISTS region TEXT;
ALTER TABLE warehouses ADD COLUMN IF NOT EXISTS country TEXT;
ALTER TABLE warehouses ADD COLUMN IF NOT EXISTS phone TEXT;
ALTER TABLE warehouses ADD COLUMN IF NOT EXISTS aisle TEXT;
ALTER TABLE warehouses ADD COLUMN IF NOT EXISTS rack TEXT;
ALTER TABLE warehouses ADD COLUMN IF NOT EXISTS shelf TEXT;
ALTER TABLE warehouses ADD COLUMN IF NOT EXISTS notes TEXT;
ALTER TABLE warehouses ADD COLUMN IF NOT EXISTS is_default INTEGER NOT NULL DEFAULT 0;
ALTER TABLE warehouses ADD COLUMN IF NOT EXISTS is_active INTEGER NOT NULL DEFAULT 1;
ALTER TABLE warehouses ADD COLUMN IF NOT EXISTS is_deleted INTEGER NOT NULL DEFAULT 0;
ALTER TABLE warehouses ADD COLUMN IF NOT EXISTS created_at TEXT;
ALTER TABLE warehouses ADD COLUMN IF NOT EXISTS updated_at TEXT;
ALTER TABLE warehouses ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ;
ALTER TABLE warehouses ADD COLUMN IF NOT EXISTS deleted_by TEXT;
CREATE UNIQUE INDEX IF NOT EXISTS ux_warehouses_company_cardno
    ON warehouses(company_id, card_no) WHERE card_no > 0 AND is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_warehouses_company
    ON warehouses(company_id, is_active) WHERE is_deleted = 0;
-- @shoutech-migration 053_valuation_engine.sql 31E0161041FD006B6FF46A8F33405C86F4D5A2A858C531BB7740E6C23FEEAB77 31E0161041FD006B6FF46A8F33405C86F4D5A2A858C531BB7740E6C23FEEAB77
-- ============================================================================
-- 053 - Stock valuation engine (PostgreSQL)
-- PostgreSQL counterpart of sqlite/053_valuation_engine.sql (Fix Phase
-- Step 6, Phase B design section 1). See the SQLite file for the design
-- notes and the numbering note (053 applies cleanly after 059-061).
-- ============================================================================

ALTER TABLE material_stock_movements ADD COLUMN IF NOT EXISTS balance_qty_after NUMERIC;
ALTER TABLE material_stock_movements ADD COLUMN IF NOT EXISTS valuation_rate NUMERIC;
ALTER TABLE material_stock_movements ADD COLUMN IF NOT EXISTS stock_value_after NUMERIC;
ALTER TABLE material_stock_movements ADD COLUMN IF NOT EXISTS cost_amount NUMERIC;
ALTER TABLE material_stock_movements ADD COLUMN IF NOT EXISTS valuation_method TEXT;
ALTER TABLE material_stock_movements ADD COLUMN IF NOT EXISTS repost_batch_id TEXT;

ALTER TABLE material_stock_balances ADD COLUMN IF NOT EXISTS stock_value NUMERIC NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS stock_cost_layers (
    layer_id BIGSERIAL PRIMARY KEY,
    company_id TEXT NOT NULL,
    warehouse_id INTEGER NOT NULL,
    material_id INTEGER NOT NULL,
    source_movement_id INTEGER,
    layer_date TEXT NOT NULL DEFAULT '',
    qty_in NUMERIC NOT NULL DEFAULT 0,
    qty_remaining NUMERIC NOT NULL DEFAULT 0,
    unit_cost NUMERIC NOT NULL DEFAULT 0,
    is_open BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_stock_cost_layers_queue
    ON stock_cost_layers(company_id, warehouse_id, material_id, is_open, layer_date, layer_id);

CREATE TABLE IF NOT EXISTS stock_revaluation_jobs (
    job_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    warehouse_id INTEGER,
    material_id INTEGER,
    from_date TEXT NOT NULL,
    reason TEXT,
    status TEXT NOT NULL DEFAULT 'Pending',
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    completed_at TIMESTAMPTZ,
    movements_replayed INTEGER NOT NULL DEFAULT 0,
    value_delta NUMERIC NOT NULL DEFAULT 0,
    error TEXT
);
CREATE INDEX IF NOT EXISTS ix_stock_revaluation_jobs_company
    ON stock_revaluation_jobs(company_id, created_at);

UPDATE material_stock_balances
SET stock_value = available_qty * average_cost
WHERE stock_value = 0 AND average_cost <> 0;

UPDATE material_stock_movements
SET valuation_rate = unit_cost,
    cost_amount = (qty_in - qty_out) * unit_cost,
    valuation_method = 'Legacy'
WHERE valuation_rate IS NULL;

INSERT INTO stock_cost_layers
    (company_id, warehouse_id, material_id, source_movement_id, layer_date,
     qty_in, qty_remaining, unit_cost, is_open)
SELECT b.company_id, b.warehouse_id, b.material_id, NULL,
       COALESCE((SELECT MAX(x.movement_date)::TEXT FROM material_stock_movements x
                  WHERE x.company_id = b.company_id
                    AND x.warehouse_id = b.warehouse_id
                    AND x.material_id = b.material_id
                    AND x.is_deleted = FALSE), CURRENT_DATE::TEXT),
       b.available_qty, b.available_qty, b.average_cost, TRUE
FROM material_stock_balances b
JOIN materials m
  ON m.company_id = b.company_id AND m.material_id = b.material_id
WHERE b.is_deleted = FALSE AND b.available_qty > 0
  AND COALESCE(m.cost_method, 'WeightedAvg') IN ('FIFO', 'LIFO')
  AND NOT EXISTS (SELECT 1 FROM stock_cost_layers l
                  WHERE l.company_id = b.company_id
                    AND l.warehouse_id = b.warehouse_id
                    AND l.material_id = b.material_id);

INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.VALUATION.VIEW','Inventory Valuation - View','INVENTORY','VALUATION','VIEW','Inventory Valuation - View','Inventory Valuation - View','View inventory valuation (Phase B 053)',340,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.VALUATION.RECALC','Inventory Valuation - Recalculate Costs','INVENTORY','VALUATION','RECALC','Inventory Valuation - Recalculate Costs','Inventory Valuation - Recalculate Costs','Run the Recalculate Costs job (Phase B 053)',341,true) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('INV.VALUATION.VIEW','INV.VALUATION.RECALC') ON CONFLICT DO NOTHING;
INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('INV.VALUATION.VIEW','INV.VALUATION.RECALC') ON CONFLICT DO NOTHING;
INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_VIEWER' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('INV.VALUATION.VIEW') ON CONFLICT DO NOTHING;
-- @shoutech-migration 054_batch_serial.sql 903C0C08567D51475336002F69095EE4EF0408A39C9CE8C3FFEDCC49BAA9C66B 903C0C08567D51475336002F69095EE4EF0408A39C9CE8C3FFEDCC49BAA9C66B
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
-- @shoutech-migration 064_document_target_warehouse.sql CC75FE321BFB2D107B884CC0354E042CE2E7ABD611C639B9BEDD1B0C387A87B2 CC75FE321BFB2D107B884CC0354E042CE2E7ABD611C639B9BEDD1B0C387A87B2
-- ============================================================================
-- 064 - Document target warehouse (PostgreSQL)
-- PostgreSQL counterpart of sqlite/064_document_target_warehouse.sql
-- (Fix Phase Step 9). See the SQLite file for the full design notes:
-- sales_documents / purchase_documents gain target_warehouse_id +
-- target_warehouse_name so an InvoiceWin StockTransfer document can
-- persist its destination warehouse for the StkPost.PostTransfer engine.
-- Idempotent: ADD COLUMN IF NOT EXISTS throughout.
-- ============================================================================

ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS target_warehouse_id INTEGER;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS target_warehouse_name TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS target_warehouse_id INTEGER;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS target_warehouse_name TEXT;
-- @shoutech-migration 076_stock_posting_operations.sql 7C1AE9F1610D8E1E0773AEA289FEDBAB420F0E4F10A5E68CA13C081FA0A18024 7C1AE9F1610D8E1E0773AEA289FEDBAB420F0E4F10A5E68CA13C081FA0A18024
-- Durable idempotency receipts for dedicated stock posting engines.
-- The receipt is committed in the same transaction as all stock movements.
CREATE TABLE IF NOT EXISTS stock_posting_operations (
    company_id TEXT NOT NULL,
    operation_key TEXT NOT NULL,
    operation_type TEXT NOT NULL,
    document_no TEXT NOT NULL,
    movement_count INTEGER NOT NULL DEFAULT 0 CHECK (movement_count >= 0),
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (company_id, operation_key)
);

ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS posting_operation_key TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS posting_operation_key TEXT;
-- @shoutech-migration 089_smdl.sql F389D190258BE58AE5B5B7FD8AFBB57EC4D8289D22F9DC52FC18D7D73A5FFB2D F389D190258BE58AE5B5B7FD8AFBB57EC4D8289D22F9DC52FC18D7D73A5FFB2D
-- 089 - Preserve the exact commercial document line on stock movements (PostgreSQL).
-- Landed-cost allocation and traceability must target the receipt line's own cost layer.
ALTER TABLE material_stock_movements
    ADD COLUMN IF NOT EXISTS source_document_line_id BIGINT;
CREATE INDEX IF NOT EXISTS ix_stock_movements_source_line
    ON material_stock_movements(company_id, source_document_line_id);
-- @shoutech-migration 089_sml.sql 4990807A110DD910ABF4C860BF59007446727E8C370FAAD5BB885849D47944B3 4990807A110DD910ABF4C860BF59007446727E8C370FAAD5BB885849D47944B3
-- 089 - Preserve the exact commercial source line on stock movements (PostgreSQL).
-- SQLite counterpart: sqlite/089_sml.sql.
ALTER TABLE material_stock_movements
    ADD COLUMN IF NOT EXISTS source_document_line_id BIGINT;
CREATE INDEX IF NOT EXISTS ix_stock_movements_source_line
    ON material_stock_movements(company_id, source_document_line_id, stock_movement_id);
-- @shoutech-migration 090_osml.sql 8514FA23F3B9B0D8FB828A1E6B925055864D6916213A2165579A92264FB2658C 8514FA23F3B9B0D8FB828A1E6B925055864D6916213A2165579A92264FB2658C
-- 090 - Persist the exact source receipt line on linked return movements (PostgreSQL).
ALTER TABLE material_stock_movements
    ADD COLUMN IF NOT EXISTS original_source_document_line_id BIGINT;
CREATE INDEX IF NOT EXISTS ix_stock_movements_original_line
    ON material_stock_movements(company_id, original_source_document_line_id, stock_movement_id);
-- @shoutech-migration 094_stock_delivered_not_billed.sql 6585EADD6DC4E0C064313D6C54F3739473D4F4633EF032E254A0A348C002F390 6585EADD6DC4E0C064313D6C54F3739473D4F4633EF032E254A0A348C002F390
-- ============================================================================
-- 094 - Stock Delivered But Not Billed account (PostgreSQL)
--
-- Sales cycle upgrade B4 (docs/SALES_CYCLE_UPGRADE.md, gap G12): under
-- perpetual inventory a posted delivery note now carries its own GL entry
--   Dr 1215 Stock Delivered But Not Billed / Cr 1203 Merchandise Inventory
-- and a delivery-linked sales invoice releases COGS against 1215
-- (Dr 401 / Cr 1215) instead of crediting inventory a second time. The
-- delivered-but-unbilled position is therefore visible on the balance
-- sheet between delivery and billing.
--
-- CODE CHOICE: the upgrade plan named this account 1204, but 1204 is
-- already "Prepaid Expenses" in every seeded COA template and
-- chart_of_accounts enforces UNIQUE(company_id, account_code) — reusing
-- 1204 would have posted delivery cost into Prepaid Expenses. 1215 is
-- the first free code in the inventory family (1210 Raw Materials,
-- 1211 WIP, 1212 Finished Goods, 1213 Spare Parts) and is used instead;
-- GlAccounts.StockDeliveredNotBilled carries the same code.
--
-- This migration adds 1215 to every EXISTING company that already has a
-- chart of accounts and lacks the code (the 058 manufacturing pattern:
-- templates carry the account for new companies, posting code resolves
-- it with TryResolveAccount and falls back to Inventory 1203 on charts
-- that have no postable 1215, so a chart this migration cannot reach
-- never breaks). The parent is the company's own group account '12'
-- (Current Assets) when present. The account id is deterministic
-- (059 pattern: md5 of company + migration + code).
--
-- Idempotent: the checksum ledger skips a re-run; inside the file the
-- INSERT is guarded by NOT EXISTS on (company_id, '1215').
-- ============================================================================

INSERT INTO chart_of_accounts
    (account_id, company_id, account_code, account_name, name_ar, account_type,
     parent_account_id, is_postable, is_active, is_deleted)
SELECT md5(c.company_id::text || ':094:1215')::uuid,
       c.company_id,
       '1215',
       'Stock Delivered But Not Billed',
       'بضاعة مسلّمة غير مفوترة',
       'ASSET',
       (SELECT g.account_id FROM chart_of_accounts g
         WHERE g.company_id = c.company_id AND g.account_code = '12'
         LIMIT 1),
       true, true, false
FROM companies c
WHERE EXISTS (SELECT 1 FROM chart_of_accounts x WHERE x.company_id = c.company_id)
  AND NOT EXISTS (SELECT 1 FROM chart_of_accounts x
                  WHERE x.company_id = c.company_id AND x.account_code = '1215');
