-- @shoutech-migration 005_inventory_indexes.sql 53B9BAB364D2D4B11CCD8BF024052CE2E8C7B14CBEC1CBE2105098756D1F3F96 BE2094F2847B85784B24DA344509FFDFC02C389BA0B1DEBCCF1A8D0BD688D46D BE2094F2847B85784B24DA344509FFDFC02C389BA0B1DEBCCF1A8D0BD688D46D BE2094F2847B85784B24DA344509FFDFC02C389BA0B1DEBCCF1A8D0BD688D46D
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

-- SQLite NUMERIC affinity does not enforce scale, but this documents the canonical precision.
-- Application validation remains responsible for limiting quantities to six decimal places.
-- @shoutech-migration 013_inventory_schema.sql 6BDBC151DD9B58899A3FFCA3E64CCCCE933A2A350C5C3774CC7684431E6E6297 799B667DE17FF1B878E9DFA3A1D1581C9630F238CA8E9DE877D309D1BDF6309C 799B667DE17FF1B878E9DFA3A1D1581C9630F238CA8E9DE877D309D1BDF6309C 799B667DE17FF1B878E9DFA3A1D1581C9630F238CA8E9DE877D309D1BDF6309C
-- Inventory schema is owned by DBA migrations. Service code must not create tables.
        CREATE TABLE IF NOT EXISTS materials (
            material_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
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
            is_stocked INTEGER NOT NULL DEFAULT 1,
            is_purchased INTEGER NOT NULL DEFAULT 1,
            is_sold INTEGER NOT NULL DEFAULT 1,
            is_produced INTEGER NOT NULL DEFAULT 0,
            is_service INTEGER NOT NULL DEFAULT 0,
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
            allow_invoice_disc INTEGER NOT NULL DEFAULT 1,
            price_includes_tax INTEGER NOT NULL DEFAULT 0,
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
            has_expiry INTEGER NOT NULL DEFAULT 0,
            shelf_life_days INTEGER NOT NULL DEFAULT 0,
            alert_before_expiry_days INTEGER NOT NULL DEFAULT 0,
            has_serial INTEGER NOT NULL DEFAULT 0,
            has_batch INTEGER NOT NULL DEFAULT 0,
            has_lot INTEGER NOT NULL DEFAULT 0,
            track_location INTEGER NOT NULL DEFAULT 0,
            quality_check_required INTEGER NOT NULL DEFAULT 0,
            is_taxable INTEGER NOT NULL DEFAULT 0,
            tax_rate NUMERIC NOT NULL DEFAULT 0,
            tax_code TEXT,
            tax_account TEXT,
            tax_type TEXT,
            is_zero_rated INTEGER NOT NULL DEFAULT 0,
            acc_inventory TEXT,
            acc_sales TEXT,
            acc_sales_return TEXT,
            acc_cogs TEXT,
            acc_purchase TEXT,
            acc_purchase_return TEXT,
            acc_expense TEXT,
            acc_wip TEXT,
            allow_sale INTEGER NOT NULL DEFAULT 1,
            allow_purchase INTEGER NOT NULL DEFAULT 1,
            allow_return INTEGER NOT NULL DEFAULT 1,
            allow_negative INTEGER NOT NULL DEFAULT 0,
            commission_pct NUMERIC NOT NULL DEFAULT 0,
            bonus_qty NUMERIC NOT NULL DEFAULT 0,
            bonus_item_code TEXT,
            min_sale_qty NUMERIC NOT NULL DEFAULT 0,
            max_sale_qty NUMERIC NOT NULL DEFAULT 0,
            sales_blocked INTEGER NOT NULL DEFAULT 0,
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
            is_deleted INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at TEXT,
            created_by TEXT,
            updated_by TEXT,
            UNIQUE(company_id, code)
        );
        CREATE INDEX IF NOT EXISTS ix_materials_code ON materials(company_id, code);
        CREATE INDEX IF NOT EXISTS ix_materials_group ON materials(company_id, group_code);
        CREATE TABLE IF NOT EXISTS material_attributes (
            attr_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, company_id TEXT NOT NULL DEFAULT '',
            material_id INTEGER NOT NULL, attr_key TEXT NOT NULL, attr_value TEXT NOT NULL DEFAULT '',
            attr_group TEXT NOT NULL DEFAULT '', sort_order INTEGER NOT NULL DEFAULT 0,
            UNIQUE(company_id, material_id, attr_key));
        CREATE TABLE IF NOT EXISTS material_extra_barcodes (
            barcode_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, company_id TEXT NOT NULL DEFAULT '',
            material_id INTEGER NOT NULL, barcode TEXT NOT NULL, barcode_type TEXT NOT NULL DEFAULT 'EAN',
            unit_name TEXT, UNIQUE(company_id, barcode));
        CREATE TABLE IF NOT EXISTS material_price_list (
            price_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, company_id TEXT NOT NULL DEFAULT '',
            material_id INTEGER NOT NULL, price_level TEXT NOT NULL, price NUMERIC NOT NULL DEFAULT 0,
            currency TEXT NOT NULL DEFAULT '', unit_name TEXT, valid_from TEXT, valid_to TEXT);
        CREATE TABLE IF NOT EXISTS material_suppliers (
            id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, company_id TEXT NOT NULL DEFAULT '',
            material_id INTEGER NOT NULL, supplier_code TEXT NOT NULL, supplier_item_code TEXT,
            lead_time_days INTEGER NOT NULL DEFAULT 0, min_order_qty NUMERIC NOT NULL DEFAULT 0,
            is_preferred INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS material_stock_balances (
            stock_balance_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            company_id TEXT NOT NULL, warehouse_id INTEGER NOT NULL, material_id INTEGER NOT NULL,
            available_qty NUMERIC NOT NULL DEFAULT 0, reserved_qty NUMERIC NOT NULL DEFAULT 0,
            average_cost NUMERIC NOT NULL DEFAULT 0, last_cost NUMERIC NOT NULL DEFAULT 0,
            last_updated TEXT NOT NULL DEFAULT (datetime('now')), is_deleted INTEGER NOT NULL DEFAULT 0,
            UNIQUE(company_id, warehouse_id, material_id));
        CREATE INDEX IF NOT EXISTS ix_material_stock_balances_wh_mat
            ON material_stock_balances(company_id, warehouse_id, material_id)
            WHERE is_deleted = 0;
        CREATE TABLE IF NOT EXISTS material_stock_movements (
            stock_movement_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            company_id TEXT NOT NULL, warehouse_id INTEGER NOT NULL, material_id INTEGER NOT NULL,
            movement_date TEXT NOT NULL, movement_type TEXT NOT NULL, document_no TEXT NOT NULL,
            qty_in NUMERIC NOT NULL DEFAULT 0, qty_out NUMERIC NOT NULL DEFAULT 0,
            unit_cost NUMERIC NOT NULL DEFAULT 0, note TEXT, is_deleted INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX IF NOT EXISTS ix_material_stock_movements_wh_mat_date
            ON material_stock_movements(company_id, warehouse_id, material_id, movement_date);
-- @if-column-missing materials.deleted_at
ALTER TABLE materials ADD COLUMN deleted_at TEXT;
-- @if-column-missing materials.deleted_by
ALTER TABLE materials ADD COLUMN deleted_by TEXT;
-- @shoutech-migration 016_material_groups_warehouses.sql 9B93D0A27C9E0C62FC1558B27310A4951E12B9A6E53894F7646C8F359D44517A 7C48A762B4356D2DF376344E736B0C20D174930DE7C27C94464AEFB7216F9992 7C48A762B4356D2DF376344E736B0C20D174930DE7C27C94464AEFB7216F9992 7C48A762B4356D2DF376344E736B0C20D174930DE7C27C94464AEFB7216F9992
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
-- @shoutech-migration 053_valuation_engine.sql A05D7BF20ECD795649421AA5DE63711BE5D89658A9C7A74D5679111633171651 1934698311C37E803072FB813E80A5074EFF7C9DC8A07E1F562E3C14DE06395C 1934698311C37E803072FB813E80A5074EFF7C9DC8A07E1F562E3C14DE06395C 1934698311C37E803072FB813E80A5074EFF7C9DC8A07E1F562E3C14DE06395C
-- ============================================================================
-- 053 - Stock valuation engine (SQLite)
--
-- Fix Phase Step 6 / Phase B design section 1 (migration 053): ONE
-- StockValuationEngine owns every stock balance mutation. This migration
-- gives the engine its storage:
--   * material_stock_movements gains the valuation record of every
--     movement: the rate actually applied (valuation_rate), the signed
--     value of the movement (cost_amount: + receipt value, - issue COGS),
--     the balance quantity and stock value AFTER the movement
--     (balance_qty_after / stock_value_after, the ERPNext stock-ledger
--     pattern that makes as-of valuation a lookup instead of a replay),
--     a snapshot of the method used (valuation_method) and the
--     recalculation batch that last rewrote the row (repost_batch_id).
--   * material_stock_balances gains stock_value: the value of the
--     available quantity, maintained by the engine on every movement, so
--     every report reads ONE value in O(1) instead of deriving its own.
--     average_cost remains the display rate (the moving-average rate for
--     WeightedAvg; stock_value / available_qty for the layer methods).
--   * stock_cost_layers: the FIFO/LIFO layer queue per
--     (company, warehouse, material). 054 (batch/serial) extends the
--     movement contract, not this table.
--   * stock_revaluation_jobs: the Recalculate Costs job record
--     (Al-Ameen regenerate-costs / ERPNext repost pattern, made manual
--     and visible per owner decision V2).
--
-- NUMBERING NOTE: 053 was reserved for valuation while the foundations
-- chain shipped 059-061 first. The migrator applies pending migrations
-- in filename order and skips by checksum, so on a database that already
-- ran 059-061 this file is simply the one pending migration; it depends
-- only on 013 (inventory), 002-004 (roles/permissions) objects, never on
-- 059-061 objects, and is written to be safe in that position.
--
-- Idempotent: column adds are guarded by the migrator directives, table
-- and index creation is IF NOT EXISTS, every backfill is self-guarding
-- (only rows the engine has not valued yet), and the checksum ledger
-- skips a re-run.
-- ============================================================================

-- ---- Movement valuation columns -------------------------------------------
-- @if-column-missing material_stock_movements.balance_qty_after
ALTER TABLE material_stock_movements ADD COLUMN balance_qty_after NUMERIC;
-- @if-column-missing material_stock_movements.valuation_rate
ALTER TABLE material_stock_movements ADD COLUMN valuation_rate NUMERIC;
-- @if-column-missing material_stock_movements.stock_value_after
ALTER TABLE material_stock_movements ADD COLUMN stock_value_after NUMERIC;
-- @if-column-missing material_stock_movements.cost_amount
ALTER TABLE material_stock_movements ADD COLUMN cost_amount NUMERIC;
-- @if-column-missing material_stock_movements.valuation_method
ALTER TABLE material_stock_movements ADD COLUMN valuation_method TEXT;
-- @if-column-missing material_stock_movements.repost_batch_id
ALTER TABLE material_stock_movements ADD COLUMN repost_batch_id TEXT;

-- ---- Balance stock value ---------------------------------------------------
-- @if-column-missing material_stock_balances.stock_value
ALTER TABLE material_stock_balances ADD COLUMN stock_value NUMERIC NOT NULL DEFAULT 0;

-- ---- FIFO/LIFO layer queue --------------------------------------------------
CREATE TABLE IF NOT EXISTS stock_cost_layers (
    layer_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    warehouse_id INTEGER NOT NULL,
    material_id INTEGER NOT NULL,
    source_movement_id INTEGER,
    layer_date TEXT NOT NULL DEFAULT '',
    qty_in NUMERIC NOT NULL DEFAULT 0,
    qty_remaining NUMERIC NOT NULL DEFAULT 0,
    unit_cost NUMERIC NOT NULL DEFAULT 0,
    is_open INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_stock_cost_layers_queue
    ON stock_cost_layers(company_id, warehouse_id, material_id, is_open, layer_date, layer_id);

-- ---- Recalculate Costs job record -------------------------------------------
CREATE TABLE IF NOT EXISTS stock_revaluation_jobs (
    job_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    warehouse_id INTEGER,
    material_id INTEGER,
    from_date TEXT NOT NULL,
    reason TEXT,
    status TEXT NOT NULL DEFAULT 'Pending',
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    completed_at TEXT,
    movements_replayed INTEGER NOT NULL DEFAULT 0,
    value_delta NUMERIC NOT NULL DEFAULT 0,
    error TEXT
);
CREATE INDEX IF NOT EXISTS ix_stock_revaluation_jobs_company
    ON stock_revaluation_jobs(company_id, created_at);

-- ---- Backfills (self-guarding; engine rows are never rewritten) -------------
-- Balance value from the pre-engine average: the best value the old
-- writers ever maintained. Rows the engine has already valued carry a
-- non-zero stock_value (or a genuinely zero value at zero average) and
-- are untouched on any re-run.
UPDATE material_stock_balances
SET stock_value = available_qty * average_cost
WHERE stock_value = 0 AND average_cost <> 0;

-- Legacy movements: the recorded unit_cost is the only rate history
-- there is (selling price on sales, zero on chain deliveries - the
-- sweep's RC-1). Stamp it as the legacy valuation so report fallbacks
-- have one definition; Recalculate Costs rewrites these rows properly.
UPDATE material_stock_movements
SET valuation_rate = unit_cost,
    cost_amount = (qty_in - qty_out) * unit_cost,
    valuation_method = 'Legacy'
WHERE valuation_rate IS NULL;

-- Opening layer for FIFO/LIFO materials that already hold stock: one
-- layer at the balance average, dated at the material's last movement,
-- so the first engine issue has a queue to consume. Materials of other
-- methods need no layers. Guarded by NOT EXISTS so a re-run (or a
-- database the engine already serves) never duplicates a layer.
INSERT INTO stock_cost_layers
    (company_id, warehouse_id, material_id, source_movement_id, layer_date,
     qty_in, qty_remaining, unit_cost, is_open)
SELECT b.company_id, b.warehouse_id, b.material_id, NULL,
       COALESCE((SELECT MAX(x.movement_date) FROM material_stock_movements x
                  WHERE x.company_id = b.company_id
                    AND x.warehouse_id = b.warehouse_id
                    AND x.material_id = b.material_id
                    AND x.is_deleted = 0), date('now')),
       b.available_qty, b.available_qty, b.average_cost, 1
FROM material_stock_balances b
JOIN materials m
  ON m.company_id = b.company_id AND m.material_id = b.material_id
WHERE b.is_deleted = 0 AND b.available_qty > 0
  AND COALESCE(m.cost_method, 'WeightedAvg') IN ('FIFO', 'LIFO')
  AND NOT EXISTS (SELECT 1 FROM stock_cost_layers l
                  WHERE l.company_id = b.company_id
                    AND l.warehouse_id = b.warehouse_id
                    AND l.material_id = b.material_id);

-- ---- Permissions (028/038 pattern) -------------------------------------------
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.VALUATION.VIEW','Inventory Valuation - View','INVENTORY','VALUATION','VIEW','Inventory Valuation - View','Inventory Valuation - View','View inventory valuation (Phase B 053)',340,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='VALUATION',operation_code='VIEW',name_ar='Inventory Valuation - View',name_en='Inventory Valuation - View',display_order=340,is_sensitive=0 WHERE permission_code='INV.VALUATION.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.VALUATION.RECALC','Inventory Valuation - Recalculate Costs','INVENTORY','VALUATION','RECALC','Inventory Valuation - Recalculate Costs','Inventory Valuation - Recalculate Costs','Run the Recalculate Costs job (Phase B 053)',341,1);
UPDATE permissions SET group_code='INVENTORY',feature_code='VALUATION',operation_code='RECALC',name_ar='Inventory Valuation - Recalculate Costs',name_en='Inventory Valuation - Recalculate Costs',display_order=341,is_sensitive=1 WHERE permission_code='INV.VALUATION.RECALC';

INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('INV.VALUATION.VIEW','INV.VALUATION.RECALC');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('INV.VALUATION.VIEW','INV.VALUATION.RECALC');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_VIEWER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('INV.VALUATION.VIEW');
-- @shoutech-migration 054_batch_serial.sql 5948DDA323C245300895EF52C37A3BE16D6C4E155CC87927774F23385CD9A7C7 7DE09ABAFF0791B46D984696432DD5FABDC940C9F84866D090A5A4431E8D88E9 7DE09ABAFF0791B46D984696432DD5FABDC940C9F84866D090A5A4431E8D88E9 7DE09ABAFF0791B46D984696432DD5FABDC940C9F84866D090A5A4431E8D88E9
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
-- @shoutech-migration 064_document_target_warehouse.sql 6002D39955FD904523F895E583181F222DDCA012EB209F2CA030F433DEEB33CF DB3812A486BDF81AA208B0AF0D5E0C4C336FB72B83081CFC2B6BAFD49EDD0851 DB3812A486BDF81AA208B0AF0D5E0C4C336FB72B83081CFC2B6BAFD49EDD0851 DB3812A486BDF81AA208B0AF0D5E0C4C336FB72B83081CFC2B6BAFD49EDD0851
-- ============================================================================
-- 064 - Document target warehouse (SQLite)
--
-- Fix Phase Step 9 (sweep invoicing-posting.md I-6, FIX_PHASE_STEP5.md
-- flag F-1): InvoiceWin's StockTransfer mode posts through the dedicated
-- two-sided engine (StkPost.PostTransfer), which needs a DESTINATION
-- warehouse. The document model has carried one for years
-- (InvDoc.TargetWarehouseId) but no column ever existed for it, so the
-- destination could not persist -- a transfer draft saved and reopened
-- lost its destination, violating the standing full-persistence rule.
-- This migration adds the target warehouse id + display name to both
-- commercial document tables so a transfer document round-trips whole.
-- No other document kind uses the columns; they stay NULL there.
--
-- Idempotent: column adds are guarded by the migrator directives; the
-- checksum ledger also skips a re-run.
-- ============================================================================

-- @if-column-missing sales_documents.target_warehouse_id
ALTER TABLE sales_documents ADD COLUMN target_warehouse_id INTEGER;
-- @if-column-missing sales_documents.target_warehouse_name
ALTER TABLE sales_documents ADD COLUMN target_warehouse_name TEXT;
-- @if-column-missing purchase_documents.target_warehouse_id
ALTER TABLE purchase_documents ADD COLUMN target_warehouse_id INTEGER;
-- @if-column-missing purchase_documents.target_warehouse_name
ALTER TABLE purchase_documents ADD COLUMN target_warehouse_name TEXT;
-- @shoutech-migration 076_stock_posting_operations.sql EC12AD07AFEBC96DEFECA940564FA2ED84A2761D59C4A2BC63BF568EC555F676 C3318AB20B2FB7A11DAE7999ABC784BFC122433F0EA5D1EC274BA8AFEADDD32E C3318AB20B2FB7A11DAE7999ABC784BFC122433F0EA5D1EC274BA8AFEADDD32E C3318AB20B2FB7A11DAE7999ABC784BFC122433F0EA5D1EC274BA8AFEADDD32E
-- Durable idempotency receipts for dedicated stock posting engines.
-- The receipt is committed in the same transaction as all stock movements.
CREATE TABLE IF NOT EXISTS stock_posting_operations (
    company_id TEXT NOT NULL,
    operation_key TEXT NOT NULL,
    operation_type TEXT NOT NULL,
    document_no TEXT NOT NULL,
    movement_count INTEGER NOT NULL DEFAULT 0 CHECK (movement_count >= 0),
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (company_id, operation_key)
);

ALTER TABLE sales_documents ADD COLUMN posting_operation_key TEXT;
ALTER TABLE purchase_documents ADD COLUMN posting_operation_key TEXT;
-- @shoutech-migration 089_smdl.sql E9E323C07801E73ED55D866EFD19E7A42B138403F7EE434D06CE680C7C11148E 828987B4B90D51736EAD42727823CEAEF94EB281012B4A7CB1679E31CAB57546 828987B4B90D51736EAD42727823CEAEF94EB281012B4A7CB1679E31CAB57546 828987B4B90D51736EAD42727823CEAEF94EB281012B4A7CB1679E31CAB57546
-- 089 - Preserve the exact commercial document line on stock movements (SQLite).
-- Landed-cost allocation and traceability must target the receipt line's own cost layer.
-- @if-column-missing material_stock_movements.source_document_line_id
ALTER TABLE material_stock_movements ADD COLUMN source_document_line_id INTEGER;
-- @endif
CREATE INDEX IF NOT EXISTS ix_stock_movements_source_line
    ON material_stock_movements(company_id, source_document_line_id);
-- @shoutech-migration 089_sml.sql 2890EE286F00AF11562319A29B35A73E9874A177D87AD4B55F0B842AB9FC3884 FBF6543B52D0285191B8B1F80FA27CD74F1E632BBF1493F9861E6ACBAD92B769 FBF6543B52D0285191B8B1F80FA27CD74F1E632BBF1493F9861E6ACBAD92B769 FBF6543B52D0285191B8B1F80FA27CD74F1E632BBF1493F9861E6ACBAD92B769
-- 089 - Preserve the exact commercial source line on stock movements (SQLite).
-- Required for landed-cost matching, returns, and traceable receipt valuation.
-- @if-column-missing material_stock_movements.source_document_line_id
ALTER TABLE material_stock_movements ADD COLUMN source_document_line_id INTEGER;
CREATE INDEX IF NOT EXISTS ix_stock_movements_source_line
    ON material_stock_movements(company_id, source_document_line_id, stock_movement_id);
-- @shoutech-migration 090_osml.sql C25472440DA24E25565736E396C0497E889955694A297A9AE68B3B2903BB448C 499EA877375A1788C263D8F055ED0CE287CD2E74C63B17F404D7C3B84A611385 499EA877375A1788C263D8F055ED0CE287CD2E74C63B17F404D7C3B84A611385 499EA877375A1788C263D8F055ED0CE287CD2E74C63B17F404D7C3B84A611385
-- 090 - Persist the exact source receipt line on linked return movements (SQLite).
-- Enables FIFO landed-cost consumption to distinguish returns of this receipt
-- from unrelated supplier returns for the same material.
-- @if-column-missing material_stock_movements.original_source_document_line_id
ALTER TABLE material_stock_movements ADD COLUMN original_source_document_line_id INTEGER;
CREATE INDEX IF NOT EXISTS ix_stock_movements_original_line
    ON material_stock_movements(company_id, original_source_document_line_id, stock_movement_id);
-- @shoutech-migration 094_stock_delivered_not_billed.sql 8B1D9F114C21AA233711448EC0FD96E5B38A4ACB1A45E4602106DC7D435BE799 D2A8918A3FCC6D43513DDBE77D0E1C929828347E5EFA06785183EB01A1A36DD8 D2A8918A3FCC6D43513DDBE77D0E1C929828347E5EFA06785183EB01A1A36DD8 D2A8918A3FCC6D43513DDBE77D0E1C929828347E5EFA06785183EB01A1A36DD8
-- ============================================================================
-- 094 - Stock Delivered But Not Billed account (SQLite)
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
-- (Current Assets) when present.
--
-- Idempotent: the checksum ledger skips a re-run; inside the file the
-- INSERT is guarded by NOT EXISTS on (company_id, '1215').
-- ============================================================================

INSERT INTO chart_of_accounts
    (account_id, company_id, account_code, account_name, name_ar, account_type,
     parent_account_id, is_postable, is_active, is_deleted)
SELECT lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' ||
       substr(lower(hex(randomblob(2))), 2) || '-a' ||
       substr(lower(hex(randomblob(2))), 2) || '-' || lower(hex(randomblob(6))),
       c.company_id,
       '1215',
       'Stock Delivered But Not Billed',
       'بضاعة مسلّمة غير مفوترة',
       'ASSET',
       (SELECT g.account_id FROM chart_of_accounts g
         WHERE g.company_id = c.company_id AND g.account_code = '12'
         LIMIT 1),
       1, 1, 0
FROM companies c
WHERE EXISTS (SELECT 1 FROM chart_of_accounts x WHERE x.company_id = c.company_id)
  AND NOT EXISTS (SELECT 1 FROM chart_of_accounts x
                  WHERE x.company_id = c.company_id AND x.account_code = '1215');
