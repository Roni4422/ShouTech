-- ============================================================
-- source: archive/legacy_schema_snapshots/lite.sql
-- ============================================================
-- ========================================================================
-- FILE: dba/lite.sql
-- PROJECT: SHOUTECH ERP V10 — SQLite Full Schema
-- VERSION: 10.10.0
-- ========================================================================

PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA temp_store = MEMORY;

BEGIN TRANSACTION;

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS companies (
    company_id              TEXT PRIMARY KEY,
    company_code            TEXT NOT NULL UNIQUE,
    company_name_ar         TEXT NOT NULL,
    company_name_en         TEXT,
    legal_name              TEXT,
    tax_number              TEXT,
    commercial_register     TEXT,
    address_line1           TEXT,
    city                    TEXT,
    country                 TEXT DEFAULT 'SA',
    phone                   TEXT,
    mobile                  TEXT,
    email                   TEXT,
    website                 TEXT,
    logo_path               TEXT,
    default_currency_code   TEXT DEFAULT 'SAR',
    default_language        TEXT DEFAULT 'ar',
    time_zone               TEXT DEFAULT 'Asia/Riyadh',
    database_name           TEXT,
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    created_by              TEXT,
    created_at              TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by              TEXT,
    updated_at              TEXT
);

CREATE TABLE IF NOT EXISTS branches (
    branch_id               TEXT PRIMARY KEY,
    company_id              TEXT NOT NULL REFERENCES companies(company_id),
    branch_code             TEXT NOT NULL,
    branch_name_ar          TEXT NOT NULL,
    branch_name_en          TEXT,
    address_line1           TEXT,
    city                    TEXT,
    phone                   TEXT,
    manager_user_id         TEXT,
    is_default              INTEGER NOT NULL DEFAULT 0,
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    created_at              TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (company_id, branch_code)
);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS users (
    user_id                 TEXT PRIMARY KEY,
    company_id              TEXT NOT NULL REFERENCES companies(company_id),
    username                TEXT NOT NULL,
    full_name_ar            TEXT NOT NULL,
    full_name_en            TEXT,
    email                   TEXT,
    mobile                  TEXT,
    password_hash           TEXT,
    password_salt           TEXT,
    employee_id             INTEGER,
    default_branch_id       TEXT REFERENCES branches(branch_id),
    is_system_admin         INTEGER NOT NULL DEFAULT 0,
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_locked               INTEGER NOT NULL DEFAULT 0,
    locked_reason           TEXT,
    last_login_at           TEXT,
    failed_login_count      INTEGER NOT NULL DEFAULT 0,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    created_at              TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at              TEXT,
    UNIQUE (company_id, username)
);

CREATE TABLE IF NOT EXISTS roles (
    role_id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL REFERENCES companies(company_id),
    role_code               TEXT NOT NULL,
    role_name_ar            TEXT NOT NULL,
    role_name_en            TEXT,
    description             TEXT,
    is_system_role          INTEGER NOT NULL DEFAULT 0,
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    created_at              TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (company_id, role_code)
);

CREATE TABLE IF NOT EXISTS permissions (
    permission_id           INTEGER PRIMARY KEY AUTOINCREMENT,
    permission_code         TEXT NOT NULL UNIQUE,
    module_name             TEXT NOT NULL,
    resource_name           TEXT,
    action_name             TEXT NOT NULL,
    description_ar          TEXT,
    description_en          TEXT,
    is_active               INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS user_roles (
    user_role_id            INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id                 TEXT NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    role_id                 INTEGER NOT NULL REFERENCES roles(role_id) ON DELETE CASCADE,
    assigned_at             TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (user_id, role_id)
);

CREATE TABLE IF NOT EXISTS role_permissions (
    role_permission_id      INTEGER PRIMARY KEY AUTOINCREMENT,
    role_id                 INTEGER NOT NULL REFERENCES roles(role_id) ON DELETE CASCADE,
    permission_id           INTEGER NOT NULL REFERENCES permissions(permission_id) ON DELETE CASCADE,
    granted                 INTEGER NOT NULL DEFAULT 1,
    granted_at              TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (role_id, permission_id)
);

CREATE TABLE IF NOT EXISTS user_permissions (
    user_permission_id      INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id                 TEXT NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    permission_id           INTEGER NOT NULL REFERENCES permissions(permission_id) ON DELETE CASCADE,
    granted                 INTEGER NOT NULL DEFAULT 1,
    granted_at              TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (user_id, permission_id)
);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS currencies (
    currency_code           TEXT PRIMARY KEY,
    currency_name_ar        TEXT NOT NULL,
    currency_name_en        TEXT,
    symbol                  TEXT,
    decimal_places          INTEGER NOT NULL DEFAULT 2,
    is_active               INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS currency_rates (
    rate_id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL REFERENCES companies(company_id),
    from_currency           TEXT NOT NULL REFERENCES currencies(currency_code),
    to_currency             TEXT NOT NULL REFERENCES currencies(currency_code),
    rate                    NUMERIC NOT NULL,
    effective_date          TEXT NOT NULL DEFAULT (date('now')),
    source                  TEXT,
    created_at              TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS tax_codes (
    tax_code_id             INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL REFERENCES companies(company_id),
    tax_code                TEXT NOT NULL,
    tax_name_ar             TEXT NOT NULL,
    tax_name_en             TEXT,
    tax_rate                NUMERIC NOT NULL DEFAULT 0,
    tax_category            TEXT NOT NULL DEFAULT 'STANDARD'
        CHECK (tax_category IN ('STANDARD','ZERO','EXEMPT')),
    zatca_item_code         TEXT,
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    created_at              TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (company_id, tax_code)
);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS account_types (
    account_type_id         INTEGER PRIMARY KEY AUTOINCREMENT,
    type_code               TEXT NOT NULL UNIQUE,
    type_name_ar            TEXT NOT NULL,
    type_name_en            TEXT NOT NULL,
    normal_balance          TEXT NOT NULL CHECK (normal_balance IN ('DEBIT','CREDIT')),
    sort_order              INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS chart_of_accounts (
    account_id              INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL REFERENCES companies(company_id),
    parent_account_id       INTEGER REFERENCES chart_of_accounts(account_id),
    account_code            TEXT NOT NULL,
    account_name_ar         TEXT NOT NULL,
    account_name_en         TEXT,
    account_type_id         INTEGER NOT NULL REFERENCES account_types(account_type_id),
    account_level           INTEGER NOT NULL DEFAULT 1,
    is_leaf                 INTEGER NOT NULL DEFAULT 1,
    currency_code           TEXT,
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    created_at              TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (company_id, account_code)
);

CREATE TABLE IF NOT EXISTS fiscal_years (
    fiscal_year_id          INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL REFERENCES companies(company_id),
    year_code               TEXT NOT NULL,
    start_date              TEXT NOT NULL,
    end_date                TEXT NOT NULL,
    status                  TEXT NOT NULL DEFAULT 'OPEN'
        CHECK (status IN ('OPEN','CLOSING','CLOSED')),
    UNIQUE (company_id, year_code)
);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS product_classes (
    class_id                INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL REFERENCES companies(company_id),
    parent_class_id         INTEGER REFERENCES product_classes(class_id),

    card_no                 INTEGER NOT NULL DEFAULT 0,

    class_code              TEXT NOT NULL,
    class_name_ar           TEXT NOT NULL,
    class_name_en           TEXT,
    class_level             INTEGER NOT NULL DEFAULT 1,

    warehouse               TEXT,
    general_specs           TEXT,
    notes                   TEXT,
    is_sales_class          INTEGER NOT NULL DEFAULT 0,

    icon_path               TEXT,
    image_path              TEXT,

    default_account_id      INTEGER REFERENCES chart_of_accounts(account_id),
    default_tax_code_id     INTEGER REFERENCES tax_codes(tax_code_id),

    is_active               INTEGER NOT NULL DEFAULT 1,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    deleted_at              TEXT,

    created_by              TEXT,
    created_at              TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by              TEXT,
    updated_at              TEXT,

    UNIQUE (company_id, class_code),
    UNIQUE (company_id, card_no)
);

CREATE INDEX IF NOT EXISTS ix_pcls_company ON product_classes(company_id, is_active) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_pcls_parent  ON product_classes(parent_class_id) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_pcls_sales   ON product_classes(company_id, is_sales_class) WHERE is_deleted = 0 AND is_sales_class = 1;

CREATE TABLE IF NOT EXISTS product_class_sequences (
    company_id              TEXT PRIMARY KEY REFERENCES companies(company_id),
    last_card_no            INTEGER NOT NULL DEFAULT 0,
    updated_at              TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS product_class_attributes (
    attribute_id            INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    class_id                INTEGER NOT NULL REFERENCES product_classes(class_id) ON DELETE CASCADE,
    attribute_key           TEXT NOT NULL,
    attribute_value         TEXT,
    attribute_group         TEXT,
    sort_order              INTEGER NOT NULL DEFAULT 0,
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    created_at              TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS product_class_images (
    image_id                INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    class_id                INTEGER NOT NULL REFERENCES product_classes(class_id) ON DELETE CASCADE,
    image_path              TEXT NOT NULL,
    image_order             INTEGER NOT NULL DEFAULT 0,
    is_primary              INTEGER NOT NULL DEFAULT 0,
    title                   TEXT,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    created_at              TEXT NOT NULL DEFAULT (datetime('now'))
);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS brands (
    brand_id                INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL REFERENCES companies(company_id),
    brand_code              TEXT NOT NULL,
    brand_name_ar           TEXT NOT NULL,
    brand_name_en           TEXT,
    description             TEXT,
    logo_path               TEXT,
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    created_at              TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (company_id, brand_code)
);

CREATE TABLE IF NOT EXISTS units_of_measure (
    uom_id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL REFERENCES companies(company_id),
    uom_code                TEXT NOT NULL,
    uom_name_ar             TEXT NOT NULL,
    uom_name_en             TEXT,
    base_uom_id             INTEGER REFERENCES units_of_measure(uom_id),
    conversion_factor       NUMERIC NOT NULL DEFAULT 1,
    is_base_unit            INTEGER NOT NULL DEFAULT 0,
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    created_at              TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (company_id, uom_code)
);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS products (
    product_id              INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL REFERENCES companies(company_id),

    product_code            TEXT NOT NULL,
    product_name_ar         TEXT NOT NULL,
    product_name_en         TEXT,
    short_name              TEXT,
    search_name             TEXT,
    description             TEXT,
    notes                   TEXT,

    class_id                INTEGER REFERENCES product_classes(class_id),
    brand_id                INTEGER REFERENCES brands(brand_id),

    primary_uom_id          INTEGER REFERENCES units_of_measure(uom_id),

    barcode                 TEXT,
    quick_code              TEXT,
    sku                     TEXT,
    gtin                    TEXT,
    alt_code                TEXT,
    old_code                TEXT,
    supplier_code           TEXT,

    cost_price              NUMERIC NOT NULL DEFAULT 0,
    price_list              NUMERIC NOT NULL DEFAULT 0,
    price_retail            NUMERIC NOT NULL DEFAULT 0,
    price_wholesale         NUMERIC NOT NULL DEFAULT 0,
    price_special           NUMERIC NOT NULL DEFAULT 0,
    price_export            NUMERIC NOT NULL DEFAULT 0,
    price_distributor       NUMERIC NOT NULL DEFAULT 0,
    price_currency          TEXT DEFAULT 'SAR',

    tax_rate                NUMERIC NOT NULL DEFAULT 0,
    is_taxable              INTEGER NOT NULL DEFAULT 0,
    price_includes_tax      INTEGER NOT NULL DEFAULT 0,

    is_stock_item           INTEGER NOT NULL DEFAULT 1,
    is_service_item         INTEGER NOT NULL DEFAULT 0,
    is_pos_item             INTEGER NOT NULL DEFAULT 1,
    is_serialized           INTEGER NOT NULL DEFAULT 0,
    is_batch_tracked        INTEGER NOT NULL DEFAULT 0,
    is_expiry_tracked       INTEGER NOT NULL DEFAULT 0,
    item_type               TEXT NOT NULL DEFAULT 'Stock',

    reorder_level           NUMERIC NOT NULL DEFAULT 0,
    safety_stock            NUMERIC NOT NULL DEFAULT 0,
    min_qty                 NUMERIC NOT NULL DEFAULT 0,
    max_qty                 NUMERIC NOT NULL DEFAULT 0,

    weight_net              NUMERIC,
    weight_gross            NUMERIC,
    weight_unit             TEXT,
    length                  NUMERIC,
    width                   NUMERIC,
    height                  NUMERIC,
    dim_unit                TEXT,
    volume                  NUMERIC,
    volume_unit             TEXT,
    area                    NUMERIC,
    area_unit               TEXT,
    pack_qty                NUMERIC,
    units_per_parcel        NUMERIC,

    main_image_path         TEXT,

    origin_country          TEXT,
    manufacturer            TEXT,
    model                   TEXT,
    color                   TEXT,
    size_spec               TEXT,
    grade                   TEXT,
    tags                    TEXT,

    acc_inventory           TEXT,
    acc_sales               TEXT,
    acc_sales_return        TEXT,
    acc_cogs                TEXT,
    acc_purchase            TEXT,
    acc_purchase_return     TEXT,
    acc_expense             TEXT,
    acc_wip                 TEXT,

    allow_sale              INTEGER NOT NULL DEFAULT 1,
    allow_purchase          INTEGER NOT NULL DEFAULT 1,
    allow_return            INTEGER NOT NULL DEFAULT 1,
    allow_negative          INTEGER NOT NULL DEFAULT 0,
    sales_blocked           INTEGER NOT NULL DEFAULT 0,
    track_location          INTEGER NOT NULL DEFAULT 0,
    quality_check_required  INTEGER NOT NULL DEFAULT 0,

    cost_method             TEXT NOT NULL DEFAULT 'WeightedAvg',
    cost_currency           TEXT,

    default_warehouse       TEXT,
    shelf_bin               TEXT,
    lead_time_days          INTEGER NOT NULL DEFAULT 0,

    -- JSON
    units_json              TEXT,
    prices_json             TEXT,
    attributes_json         TEXT,

    status                  TEXT NOT NULL DEFAULT 'Active',
    is_active               INTEGER NOT NULL DEFAULT 1,

    is_deleted              INTEGER NOT NULL DEFAULT 0,
    deleted_at              TEXT,
    created_by              TEXT,
    created_at              TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by              TEXT,
    updated_at              TEXT,

    UNIQUE (company_id, product_code)
);

CREATE INDEX IF NOT EXISTS ix_prod_company ON products(company_id, is_active) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_prod_barcode ON products(company_id, barcode) WHERE barcode IS NOT NULL AND is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_prod_class   ON products(class_id, is_active) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_prod_brand   ON products(brand_id, is_active) WHERE is_deleted = 0;


CREATE TABLE IF NOT EXISTS product_units (
    product_unit_id         INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    product_id              INTEGER NOT NULL REFERENCES products(product_id) ON DELETE CASCADE,
    uom_id                  INTEGER REFERENCES units_of_measure(uom_id),
    unit_name               TEXT NOT NULL,
    factor                  NUMERIC NOT NULL DEFAULT 1,
    barcode                 TEXT,
    line_no                 INTEGER NOT NULL DEFAULT 1,
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_deleted              INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS product_barcodes (
    barcode_id              INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    product_id              INTEGER NOT NULL REFERENCES products(product_id) ON DELETE CASCADE,
    barcode                 TEXT NOT NULL,
    barcode_type            TEXT NOT NULL DEFAULT 'EAN',
    unit_name               TEXT,
    is_primary              INTEGER NOT NULL DEFAULT 0,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    UNIQUE (company_id, barcode)
);

CREATE TABLE IF NOT EXISTS product_suppliers (
    product_supplier_id     INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    product_id              INTEGER NOT NULL REFERENCES products(product_id) ON DELETE CASCADE,
    supplier_code           TEXT NOT NULL,
    supplier_item_code      TEXT,
    lead_time_days          INTEGER NOT NULL DEFAULT 0,
    min_order_qty           NUMERIC NOT NULL DEFAULT 0,
    is_preferred            INTEGER NOT NULL DEFAULT 0,
    is_deleted              INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS product_attributes (
    attribute_id            INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    product_id              INTEGER NOT NULL REFERENCES products(product_id) ON DELETE CASCADE,
    attribute_key           TEXT NOT NULL,
    attribute_value         TEXT,
    attribute_group         TEXT,
    sort_order              INTEGER NOT NULL DEFAULT 0,
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    UNIQUE (company_id, product_id, attribute_key)
);

CREATE TABLE IF NOT EXISTS product_images (
    image_id                INTEGER PRIMARY KEY AUTOINCREMENT,
    product_id              INTEGER NOT NULL REFERENCES products(product_id) ON DELETE CASCADE,
    image_path              TEXT NOT NULL,
    image_order             INTEGER NOT NULL DEFAULT 0,
    is_primary              INTEGER NOT NULL DEFAULT 0,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    created_at              TEXT NOT NULL DEFAULT (datetime('now'))
);

-- ========================================================================
-- [8]  — warehouses
-- ========================================================================

CREATE TABLE IF NOT EXISTS warehouse_types (
    warehouse_type_id       INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL REFERENCES companies(company_id),
    type_code               TEXT NOT NULL,
    type_name_ar            TEXT NOT NULL,
    type_name_en            TEXT,
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    UNIQUE (company_id, type_code)
);

CREATE TABLE IF NOT EXISTS warehouses (
    warehouse_id            INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL REFERENCES companies(company_id),
    branch_id               TEXT REFERENCES branches(branch_id),
    warehouse_type_id       INTEGER REFERENCES warehouse_types(warehouse_type_id),

    card_no                 INTEGER NOT NULL DEFAULT 0,

    warehouse_code          TEXT NOT NULL,
    warehouse_name_ar       TEXT NOT NULL,
    warehouse_name_en       TEXT,

    parent_code             TEXT,

    address_line1           TEXT,
    city                    TEXT,
    region                  TEXT,
    country                 TEXT DEFAULT 'SA',
    postal_code             TEXT,

    phone                   TEXT,
    mobile                  TEXT,
    email                   TEXT,

    aisle                   TEXT,
    rack                    TEXT,
    shelf                   TEXT,

    notes                   TEXT,

    is_default              INTEGER NOT NULL DEFAULT 0,
    allow_negative_stock    INTEGER NOT NULL DEFAULT 0,
    auto_reserve_on_receipt INTEGER NOT NULL DEFAULT 1,

    is_active               INTEGER NOT NULL DEFAULT 1,
    is_frozen               INTEGER NOT NULL DEFAULT 0,

    is_deleted              INTEGER NOT NULL DEFAULT 0,
    created_by              TEXT,
    created_at              TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at              TEXT,
    updated_by              TEXT,

    UNIQUE (company_id, warehouse_code),
    UNIQUE (company_id, card_no)
);

CREATE INDEX IF NOT EXISTS ix_wh_company ON warehouses(company_id, is_active) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_wh_default ON warehouses(company_id, is_default) WHERE is_deleted = 0 AND is_default = 1;
CREATE INDEX IF NOT EXISTS ix_wh_parent  ON warehouses(parent_code) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS warehouse_sequences (
    company_id   TEXT PRIMARY KEY REFERENCES companies(company_id),
    last_card_no INTEGER NOT NULL DEFAULT 0,
    updated_at   TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS warehouse_zones (
    zone_id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    warehouse_id            INTEGER NOT NULL REFERENCES warehouses(warehouse_id) ON DELETE CASCADE,
    zone_code               TEXT NOT NULL,
    zone_name_ar            TEXT NOT NULL,
    zone_name_en            TEXT,
    zone_type               TEXT NOT NULL
        CHECK (zone_type IN ('RECEIVING','STORAGE','PICKING','PACKING','SHIPPING','QUARANTINE','DAMAGED')),
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    UNIQUE (warehouse_id, zone_code)
);

CREATE TABLE IF NOT EXISTS warehouse_locations (
    location_id             INTEGER PRIMARY KEY AUTOINCREMENT,
    warehouse_id            INTEGER NOT NULL REFERENCES warehouses(warehouse_id) ON DELETE CASCADE,
    zone_id                 INTEGER REFERENCES warehouse_zones(zone_id),
    location_code           TEXT NOT NULL,
    location_name           TEXT,
    aisle                   TEXT,
    rack                    TEXT,
    shelf                   TEXT,
    bin                     TEXT,
    location_type           TEXT NOT NULL DEFAULT 'STORAGE'
        CHECK (location_type IN ('STORAGE','PICKING','BULK','DAMAGED','QUARANTINE')),
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_blocked              INTEGER NOT NULL DEFAULT 0,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    UNIQUE (warehouse_id, location_code)
);
-- ========================================================================
-- [9] Inventory
-- ========================================================================

CREATE TABLE IF NOT EXISTS stock_balances (
    stock_balance_id        INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    warehouse_id            INTEGER NOT NULL REFERENCES warehouses(warehouse_id),
    location_id             INTEGER REFERENCES warehouse_locations(location_id),
    product_id              INTEGER NOT NULL REFERENCES products(product_id),

    available_qty           NUMERIC NOT NULL DEFAULT 0,
    reserved_qty            NUMERIC NOT NULL DEFAULT 0,
    damaged_qty             NUMERIC NOT NULL DEFAULT 0,
    on_order_qty            NUMERIC NOT NULL DEFAULT 0,

    average_cost            NUMERIC,
    last_cost               NUMERIC,

    last_updated            TEXT NOT NULL DEFAULT (datetime('now')),
    is_deleted              INTEGER NOT NULL DEFAULT 0,

    UNIQUE (company_id, warehouse_id, location_id, product_id)
);

CREATE INDEX IF NOT EXISTS ix_sb_wh_prod  ON stock_balances(warehouse_id, product_id) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_sb_available ON stock_balances(available_qty) WHERE available_qty > 0 AND is_deleted = 0;

CREATE TABLE IF NOT EXISTS stock_movements (
    movement_id             INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    movement_type           TEXT NOT NULL
        CHECK (movement_type IN ('PURCHASE','SALE','RETURN_PURCHASE','RETURN_SALE',
                                 'TRANSFER_IN','TRANSFER_OUT','ADJUSTMENT',
                                 'COUNT_OPEN','COUNT_CLOSE','REVERSAL')),
    movement_date           TEXT NOT NULL DEFAULT (datetime('now')),
    warehouse_id_from       INTEGER REFERENCES warehouses(warehouse_id),
    warehouse_id_to         INTEGER REFERENCES warehouses(warehouse_id),
    product_id              INTEGER NOT NULL REFERENCES products(product_id),
    quantity                NUMERIC NOT NULL,
    unit_cost               NUMERIC,
    total_cost              NUMERIC,
    batch_number            TEXT,
    serial_number           TEXT,
    reference_table         TEXT,
    reference_id            INTEGER,
    reference_number        TEXT,
    notes                   TEXT,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    created_by              TEXT,
    created_at              TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS ix_sm_product ON stock_movements(product_id, movement_date DESC) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_sm_ref     ON stock_movements(reference_table, reference_id) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS material_stock_balances (
    stock_balance_id        INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    warehouse_id            INTEGER NOT NULL,
    material_id             INTEGER NOT NULL,
    available_qty           NUMERIC NOT NULL DEFAULT 0,
    reserved_qty            NUMERIC NOT NULL DEFAULT 0,
    average_cost            NUMERIC NOT NULL DEFAULT 0,
    last_cost               NUMERIC NOT NULL DEFAULT 0,
    last_updated            TEXT NOT NULL DEFAULT (datetime('now')),
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    UNIQUE (company_id, warehouse_id, material_id)
);

CREATE INDEX IF NOT EXISTS ix_material_stock_balances_wh_mat
    ON material_stock_balances(company_id, warehouse_id, material_id)
    WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS material_stock_movements (
    stock_movement_id       INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    warehouse_id            INTEGER NOT NULL,
    material_id             INTEGER NOT NULL,
    movement_date           TEXT NOT NULL,
    movement_type           TEXT NOT NULL,
    document_no             TEXT NOT NULL,
    qty_in                  NUMERIC NOT NULL DEFAULT 0,
    qty_out                 NUMERIC NOT NULL DEFAULT 0,
    unit_cost               NUMERIC NOT NULL DEFAULT 0,
    note                    TEXT,
    is_deleted              INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS ix_material_stock_movements_wh_mat_date
    ON material_stock_movements(company_id, warehouse_id, material_id, movement_date);

-- ========================================================================
-- [10] Seed Data 
-- ========================================================================

INSERT OR IGNORE INTO account_types (type_code, type_name_ar, type_name_en, normal_balance, sort_order) VALUES
    ('ASSET',     'Assets',       'Assets',      'DEBIT',  1),
    ('LIABILITY', 'Liabilities',   'Liabilities', 'CREDIT', 2),
    ('EQUITY',    'Equity', 'Equity',      'CREDIT', 3),
    ('REVENUE',   'Revenue',    'Revenue',     'CREDIT', 4),
    ('EXPENSE',   'Expenses',    'Expenses',    'DEBIT',  5);

INSERT OR IGNORE INTO currencies (currency_code, currency_name_ar, currency_name_en, symbol, decimal_places) VALUES
    ('SAR', 'Saudi Riyal',   'Saudi Riyal',   'SAR', 2),
    ('USD', 'US Dollar', 'US Dollar',     '$',   2),
    ('EUR', 'Euro',         'Euro',          '€',   2),
    ('SYP', 'Syrian Pound',   'Syrian Pound',  'SYP', 2),
    ('TRY', 'Turkish Lira',   'Turkish Lira',  '₺',   2),
    ('AED', 'UAE Dirham', 'UAE Dirham',    'AED', 2);


-- ========================================================================
-- FILE: dba/lite_part2.sql
-- ========================================================================

-- ========================================================================
-- [11] Payments
-- ========================================================================

CREATE TABLE IF NOT EXISTS batches (
    batch_id                INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    product_id              INTEGER NOT NULL REFERENCES products(product_id),
    warehouse_id            INTEGER NOT NULL REFERENCES warehouses(warehouse_id),
    location_id             INTEGER REFERENCES warehouse_locations(location_id),
    batch_number            TEXT NOT NULL,
    supplier_batch_number   TEXT,
    manufacturing_date      TEXT,
    expiry_date             TEXT,
    received_date           TEXT NOT NULL DEFAULT (date('now')),
    original_quantity       NUMERIC NOT NULL,
    current_quantity        NUMERIC NOT NULL,
    reserved_quantity       NUMERIC NOT NULL DEFAULT 0,
    purchase_cost           NUMERIC,
    average_cost            NUMERIC,
    supplier_invoice_number TEXT,
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_fully_consumed       INTEGER NOT NULL DEFAULT 0,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    created_by              TEXT,
    created_at              TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by              TEXT,
    updated_at              TEXT,
    UNIQUE (company_id, product_id, batch_number)
);

CREATE INDEX IF NOT EXISTS ix_batches_expiry ON batches(expiry_date, product_id) WHERE is_active = 1 AND is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_batches_product ON batches(product_id, warehouse_id) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS serial_numbers (
    serial_id               INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    product_id              INTEGER NOT NULL REFERENCES products(product_id),
    warehouse_id            INTEGER NOT NULL REFERENCES warehouses(warehouse_id),
    location_id             INTEGER REFERENCES warehouse_locations(location_id),
    batch_id                INTEGER REFERENCES batches(batch_id),
    serial_number           TEXT NOT NULL,
    status                  TEXT NOT NULL DEFAULT 'IN_STOCK'
        CHECK (status IN ('IN_STOCK','RESERVED','SOLD','RETURNED','DAMAGED','LOST')),
    received_date           TEXT NOT NULL DEFAULT (date('now')),
    sold_date               TEXT,
    customer_id             TEXT,
    purchase_invoice_id     INTEGER,
    sales_invoice_id        INTEGER,
    last_transaction_date   TEXT,
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    created_at              TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at              TEXT,
    UNIQUE (company_id, product_id, serial_number)
);

CREATE INDEX IF NOT EXISTS ix_serial_status ON serial_numbers(status, product_id) WHERE is_active = 1 AND is_deleted = 0;

-- ========================================================================
-- [12]  (FIFO/LIFO/AVG)
-- ========================================================================

CREATE TABLE IF NOT EXISTS valuation_methods (
    valuation_method_id     INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL REFERENCES companies(company_id),
    method_code             TEXT NOT NULL,
    method_name_ar          TEXT NOT NULL,
    method_name_en          TEXT,
    method_description      TEXT,
    is_default              INTEGER NOT NULL DEFAULT 0,
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    created_at              TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (company_id, method_code)
);

CREATE TABLE IF NOT EXISTS product_valuation (
    product_valuation_id    INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    product_id              INTEGER NOT NULL REFERENCES products(product_id),
    valuation_method_id     INTEGER NOT NULL REFERENCES valuation_methods(valuation_method_id),
    effective_date          TEXT NOT NULL DEFAULT (date('now')),
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    created_at              TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (company_id, product_id, effective_date)
);

CREATE TABLE IF NOT EXISTS cost_layers (
    layer_id                INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    product_id              INTEGER NOT NULL REFERENCES products(product_id),
    warehouse_id            INTEGER NOT NULL REFERENCES warehouses(warehouse_id),
    batch_id                INTEGER REFERENCES batches(batch_id),
    layer_date              TEXT NOT NULL DEFAULT (date('now')),
    layer_sequence          INTEGER NOT NULL DEFAULT 0,
    quantity                NUMERIC NOT NULL,
    unit_cost               NUMERIC NOT NULL,
    remaining_quantity      NUMERIC NOT NULL,
    currency_code           TEXT DEFAULT 'SAR',
    source_reference        TEXT,
    source_id               INTEGER,
    source_movement_id      INTEGER REFERENCES stock_movements(movement_id),
    is_consumed             INTEGER NOT NULL DEFAULT 0,
    consumed_at             TEXT,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    created_by              TEXT,
    created_at              TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS ix_cost_layers_active
    ON cost_layers(product_id, warehouse_id, layer_date)
    WHERE is_consumed = 0 AND is_deleted = 0;

CREATE TABLE IF NOT EXISTS valuation_history (
    valuation_history_id    INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    product_id              INTEGER NOT NULL REFERENCES products(product_id),
    warehouse_id            INTEGER NOT NULL REFERENCES warehouses(warehouse_id),
    valuation_date          TEXT NOT NULL,
    valuation_method_id     INTEGER REFERENCES valuation_methods(valuation_method_id),
    total_quantity          NUMERIC NOT NULL,
    total_value             NUMERIC NOT NULL,
    average_cost            NUMERIC NOT NULL,
    currency_code           TEXT DEFAULT 'SAR',
    notes                   TEXT,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    created_at              TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS ix_val_history_product ON valuation_history(product_id, valuation_date DESC) WHERE is_deleted = 0;

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS stock_reservations (
    reservation_id          INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    stock_balance_id        INTEGER REFERENCES stock_balances(stock_balance_id),
    batch_id                INTEGER REFERENCES batches(batch_id),
    serial_id               INTEGER REFERENCES serial_numbers(serial_id),
    reference_table         TEXT NOT NULL,
    reference_id            INTEGER NOT NULL,
    reference_line_id       INTEGER,
    reference_number        TEXT,
    reserved_quantity       NUMERIC NOT NULL,
    original_quantity       NUMERIC NOT NULL,
    released_quantity       NUMERIC NOT NULL DEFAULT 0,
    reserved_at             TEXT NOT NULL DEFAULT (datetime('now')),
    reserved_by             TEXT,
    released_at             TEXT,
    released_by             TEXT,
    status                  TEXT NOT NULL DEFAULT 'ACTIVE'
        CHECK (status IN ('ACTIVE','PARTIAL','RELEASED','EXPIRED')),
    expiry_date             TEXT,
    notes                   TEXT,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    created_at              TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS ix_reservations_ref ON stock_reservations(reference_table, reference_id, status) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS stock_adjustments (
    adjustment_id           INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    adjustment_number       TEXT NOT NULL,
    adjustment_date         TEXT NOT NULL DEFAULT (date('now')),
    warehouse_id            INTEGER NOT NULL REFERENCES warehouses(warehouse_id),
    adjustment_type         TEXT NOT NULL
        CHECK (adjustment_type IN ('INCREASE','DECREASE','CORRECTION')),
    reason                  TEXT NOT NULL,
    total_adjustment        NUMERIC NOT NULL DEFAULT 0,
    status                  TEXT NOT NULL DEFAULT 'DRAFT'
        CHECK (status IN ('DRAFT','POSTED','CANCELLED')),
    posted_at               TEXT,
    posted_by               TEXT,
    cancelled_at            TEXT,
    cancelled_by            TEXT,
    notes                   TEXT,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    created_by              TEXT,
    created_at              TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (company_id, adjustment_number)
);

CREATE TABLE IF NOT EXISTS stock_adjustment_items (
    adjustment_item_id      INTEGER PRIMARY KEY AUTOINCREMENT,
    adjustment_id           INTEGER NOT NULL REFERENCES stock_adjustments(adjustment_id) ON DELETE CASCADE,
    product_id              INTEGER NOT NULL REFERENCES products(product_id),
    batch_id                INTEGER REFERENCES batches(batch_id),
    location_id             INTEGER REFERENCES warehouse_locations(location_id),
    old_quantity            NUMERIC NOT NULL,
    new_quantity            NUMERIC NOT NULL,
    difference_quantity     NUMERIC NOT NULL,
    unit_cost               NUMERIC,
    notes                   TEXT,
    is_deleted              INTEGER NOT NULL DEFAULT 0
);

-- ========================================================================
-- [14] (POS)
-- ========================================================================

CREATE TABLE IF NOT EXISTS pos_terminals (
    terminal_id             INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL REFERENCES companies(company_id),
    branch_id               TEXT REFERENCES branches(branch_id),
    terminal_code           TEXT NOT NULL,
    terminal_name_ar        TEXT NOT NULL,
    terminal_name_en        TEXT,
    location                TEXT,
    warehouse_id            INTEGER REFERENCES warehouses(warehouse_id),
    cash_account_code       TEXT,
    receipt_printer         TEXT,
    kitchen_printer         TEXT,
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    created_at              TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (company_id, terminal_code)
);

CREATE TABLE IF NOT EXISTS pos_sessions (
    session_id              INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    terminal_id             INTEGER NOT NULL REFERENCES pos_terminals(terminal_id),
    opened_by               TEXT NOT NULL,
    opened_at               TEXT NOT NULL DEFAULT (datetime('now')),
    closed_by               TEXT,
    closed_at               TEXT,
    opening_balance         NUMERIC NOT NULL DEFAULT 0,
    expected_closing_balance NUMERIC,
    actual_closing_balance  NUMERIC,
    total_sales             NUMERIC DEFAULT 0,
    total_returns           NUMERIC DEFAULT 0,
    total_cash              NUMERIC DEFAULT 0,
    total_card              NUMERIC DEFAULT 0,
    total_credit            NUMERIC DEFAULT 0,
    total_discount          NUMERIC DEFAULT 0,
    status                  TEXT NOT NULL DEFAULT 'OPEN'
        CHECK (status IN ('OPEN','CLOSED','SUSPENDED','RECONCILED')),
    notes                   TEXT,
    is_deleted              INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS pos_transactions (
    pos_transaction_id      INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    terminal_id             INTEGER NOT NULL REFERENCES pos_terminals(terminal_id),
    session_id              INTEGER NOT NULL REFERENCES pos_sessions(session_id),
    invoice_id              INTEGER,
    transaction_type        TEXT NOT NULL
        CHECK (transaction_type IN ('SALE','RETURN','VOID','QUOTATION')),
    transaction_number      TEXT NOT NULL,
    customer_id             TEXT,
    total_amount            NUMERIC NOT NULL,
    total_discount          NUMERIC DEFAULT 0,
    net_amount              NUMERIC NOT NULL,
    paid_amount             NUMERIC NOT NULL,
    change_amount           NUMERIC DEFAULT 0,
    status                  TEXT DEFAULT 'COMPLETED',
    is_deleted              INTEGER DEFAULT 0,
    created_by              TEXT,
    created_at              TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS pos_transaction_items (
    pos_transaction_item_id INTEGER PRIMARY KEY AUTOINCREMENT,
    pos_transaction_id      INTEGER NOT NULL REFERENCES pos_transactions(pos_transaction_id) ON DELETE CASCADE,
    product_id              INTEGER NOT NULL REFERENCES products(product_id),
    batch_id                INTEGER REFERENCES batches(batch_id),
    serial_id               INTEGER REFERENCES serial_numbers(serial_id),
    quantity                NUMERIC NOT NULL,
    unit_price              NUMERIC NOT NULL,
    discount                NUMERIC DEFAULT 0,
    total_amount            NUMERIC NOT NULL,
    is_deleted              INTEGER DEFAULT 0
);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS customer_groups (
    group_id                INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    group_code              TEXT NOT NULL,
    group_name_ar           TEXT NOT NULL,
    group_name_en           TEXT,
    discount_pct            NUMERIC DEFAULT 0,
    price_list_id           INTEGER REFERENCES price_lists(price_list_id),
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    UNIQUE (company_id, group_code)
);

CREATE TABLE IF NOT EXISTS customers (
    customer_id             INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL REFERENCES companies(company_id),
    customer_code           TEXT NOT NULL,
    customer_name_ar        TEXT NOT NULL,
    customer_name_en        TEXT,
    group_id                INTEGER REFERENCES customer_groups(group_id),
    customer_type           TEXT DEFAULT 'INDIVIDUAL'
        CHECK (customer_type IN ('INDIVIDUAL','COMPANY','GOVERNMENT')),
    tax_number              TEXT,
    commercial_register     TEXT,
    phone                   TEXT,
    mobile                  TEXT,
    email                   TEXT,
    website                 TEXT,
    address_line1           TEXT,
    city                    TEXT,
    country                 TEXT DEFAULT 'SA',
    credit_limit            NUMERIC DEFAULT 0,
    credit_days             INTEGER DEFAULT 0,
    opening_balance         NUMERIC DEFAULT 0,
    current_balance         NUMERIC DEFAULT 0,
    sales_rep_id            INTEGER,
    price_list_id           INTEGER REFERENCES price_lists(price_list_id),
    payment_terms           TEXT,
    is_blocked              INTEGER NOT NULL DEFAULT 0,
    block_reason            TEXT,
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    created_by              TEXT,
    created_at              TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by              TEXT,
    updated_at              TEXT,
    UNIQUE (company_id, customer_code)
);

CREATE INDEX IF NOT EXISTS ix_customers_company ON customers(company_id, is_active) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_customers_name    ON customers(customer_name_ar) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS customer_contacts (
    contact_id              INTEGER PRIMARY KEY AUTOINCREMENT,
    customer_id             INTEGER NOT NULL REFERENCES customers(customer_id) ON DELETE CASCADE,
    contact_name            TEXT NOT NULL,
    job_title               TEXT,
    phone                   TEXT,
    mobile                  TEXT,
    email                   TEXT,
    is_primary              INTEGER NOT NULL DEFAULT 0,
    is_deleted              INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS customer_addresses (
    address_id              INTEGER PRIMARY KEY AUTOINCREMENT,
    customer_id             INTEGER NOT NULL REFERENCES customers(customer_id) ON DELETE CASCADE,
    address_label           TEXT,
    address_line1           TEXT NOT NULL,
    address_line2           TEXT,
    city                    TEXT,
    region                  TEXT,
    country                 TEXT DEFAULT 'SA',
    postal_code             TEXT,
    latitude                NUMERIC,
    longitude               NUMERIC,
    is_default              INTEGER NOT NULL DEFAULT 0,
    is_deleted              INTEGER NOT NULL DEFAULT 0
);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS supplier_groups (
    group_id                INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    group_code              TEXT NOT NULL,
    group_name_ar           TEXT NOT NULL,
    group_name_en           TEXT,
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    UNIQUE (company_id, group_code)
);

CREATE TABLE IF NOT EXISTS suppliers (
    supplier_id             INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL REFERENCES companies(company_id),
    supplier_code           TEXT NOT NULL,
    supplier_name_ar        TEXT NOT NULL,
    supplier_name_en        TEXT,
    group_id                INTEGER REFERENCES supplier_groups(group_id),
    supplier_type           TEXT DEFAULT 'COMPANY'
        CHECK (supplier_type IN ('INDIVIDUAL','COMPANY','GOVERNMENT')),
    tax_number              TEXT,
    commercial_register     TEXT,
    phone                   TEXT,
    mobile                  TEXT,
    email                   TEXT,
    website                 TEXT,
    address_line1           TEXT,
    city                    TEXT,
    country                 TEXT DEFAULT 'SA',
    credit_limit            NUMERIC DEFAULT 0,
    credit_days             INTEGER DEFAULT 0,
    opening_balance         NUMERIC DEFAULT 0,
    current_balance         NUMERIC DEFAULT 0,
    payment_terms           TEXT,
    is_blocked              INTEGER NOT NULL DEFAULT 0,
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    created_by              TEXT,
    created_at              TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by              TEXT,
    updated_at              TEXT,
    UNIQUE (company_id, supplier_code)
);

CREATE INDEX IF NOT EXISTS ix_suppliers_company ON suppliers(company_id, is_active) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS supplier_contacts (
    contact_id              INTEGER PRIMARY KEY AUTOINCREMENT,
    supplier_id             INTEGER NOT NULL REFERENCES suppliers(supplier_id) ON DELETE CASCADE,
    contact_name            TEXT NOT NULL,
    job_title               TEXT,
    phone                   TEXT,
    mobile                  TEXT,
    email                   TEXT,
    is_primary              INTEGER NOT NULL DEFAULT 0,
    is_deleted              INTEGER NOT NULL DEFAULT 0
);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS sales_reps (
    sales_rep_id            INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL REFERENCES companies(company_id),
    rep_code                TEXT NOT NULL,
    rep_name_ar             TEXT NOT NULL,
    rep_name_en             TEXT,
    phone                   TEXT,
    mobile                  TEXT,
    email                   TEXT,
    manager_user_id         TEXT,
    commission_pct          NUMERIC DEFAULT 0,
    target_amount           NUMERIC DEFAULT 0,
    area_code               TEXT,
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    created_by              TEXT,
    created_at              TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (company_id, rep_code)
);

CREATE TABLE IF NOT EXISTS sales_rep_targets (
    target_id               INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    sales_rep_id            INTEGER NOT NULL REFERENCES sales_reps(sales_rep_id),
    target_year             INTEGER NOT NULL,
    target_month            INTEGER,
    target_amount           NUMERIC NOT NULL,
    achieved_amount         NUMERIC DEFAULT 0,
    commission_pct          NUMERIC DEFAULT 0,
    notes                   TEXT,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    UNIQUE (company_id, sales_rep_id, target_year, target_month)
);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS departments (
    department_id           INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    department_code         TEXT NOT NULL,
    department_name_ar      TEXT NOT NULL,
    department_name_en      TEXT,
    parent_department_id    INTEGER REFERENCES departments(department_id),
    manager_employee_id     INTEGER,
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    UNIQUE (company_id, department_code)
);

CREATE TABLE IF NOT EXISTS job_titles (
    job_title_id            INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    title_code              TEXT NOT NULL,
    title_name_ar           TEXT NOT NULL,
    title_name_en           TEXT,
    job_level               INTEGER DEFAULT 1,
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    UNIQUE (company_id, title_code)
);

CREATE TABLE IF NOT EXISTS employees (
    employee_id             INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL REFERENCES companies(company_id),
    employee_code           TEXT NOT NULL,
    full_name_ar            TEXT NOT NULL,
    full_name_en            TEXT,
    national_id             TEXT,
    birth_date              TEXT,
    gender                  TEXT CHECK (gender IN ('M','F')),
    marital_status          TEXT,
    nationality             TEXT,
    phone                   TEXT,
    mobile                  TEXT,
    email                   TEXT,
    address_line1           TEXT,
    city                    TEXT,
    department_id           INTEGER REFERENCES departments(department_id),
    job_title_id            INTEGER REFERENCES job_titles(job_title_id),
    branch_id               TEXT REFERENCES branches(branch_id),
    warehouse_id            INTEGER REFERENCES warehouses(warehouse_id),
    hire_date               TEXT,
    end_date                TEXT,
    basic_salary            NUMERIC DEFAULT 0,
    allowances              NUMERIC DEFAULT 0,
    bank_account_id         INTEGER,
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    created_by              TEXT,
    created_at              TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at              TEXT,
    UNIQUE (company_id, employee_code)
);

CREATE INDEX IF NOT EXISTS ix_employees_company ON employees(company_id, is_active) WHERE is_deleted = 0;

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS banks (
    bank_id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL REFERENCES companies(company_id),
    bank_code               TEXT NOT NULL,
    bank_name_ar            TEXT NOT NULL,
    bank_name_en            TEXT,
    swift_code              TEXT,
    branch_name             TEXT,
    phone                   TEXT,
    email                   TEXT,
    website                 TEXT,
    address_line1           TEXT,
    city                    TEXT,
    country                 TEXT DEFAULT 'SA',
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    created_at              TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (company_id, bank_code)
);

CREATE TABLE IF NOT EXISTS bank_accounts (
    bank_account_id         INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    bank_id                 INTEGER NOT NULL REFERENCES banks(bank_id),
    account_number          TEXT NOT NULL,
    account_name            TEXT NOT NULL,
    iban                    TEXT,
    currency_code           TEXT DEFAULT 'SAR',
    account_type            TEXT CHECK (account_type IN ('CURRENT','SAVINGS','DEPOSIT','LOAN')),
    gl_account_id           INTEGER REFERENCES chart_of_accounts(account_id),
    opening_balance         NUMERIC DEFAULT 0,
    current_balance         NUMERIC DEFAULT 0,
    is_default              INTEGER NOT NULL DEFAULT 0,
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    UNIQUE (company_id, bank_id, account_number)
);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS work_centers (
    work_center_id          INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    center_code             TEXT NOT NULL,
    center_name_ar          TEXT NOT NULL,
    center_name_en          TEXT,
    hourly_rate             NUMERIC DEFAULT 0,
    capacity_per_hour       NUMERIC DEFAULT 0,
    is_active               INTEGER NOT NULL DEFAULT 1,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    UNIQUE (company_id, center_code)
);

CREATE TABLE IF NOT EXISTS production_orders (
    production_order_id     INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    order_number            TEXT NOT NULL,
    order_date              TEXT NOT NULL DEFAULT (date('now')),
    product_id              INTEGER NOT NULL REFERENCES products(product_id),
    planned_quantity        NUMERIC NOT NULL,
    produced_quantity       NUMERIC DEFAULT 0,
    start_date              TEXT,
    end_date                TEXT,
    warehouse_id            INTEGER REFERENCES warehouses(warehouse_id),
    bom_id                  INTEGER REFERENCES product_bom(bom_id),
    status                  TEXT NOT NULL DEFAULT 'DRAFT'
        CHECK (status IN ('DRAFT','RELEASED','IN_PROGRESS','COMPLETED','CANCELLED')),
    total_cost              NUMERIC DEFAULT 0,
    notes                   TEXT,
    is_deleted              INTEGER NOT NULL DEFAULT 0,
    created_by              TEXT,
    created_at              TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (company_id, order_number)
);

CREATE TABLE IF NOT EXISTS production_order_lines (
    line_id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    production_order_id     INTEGER NOT NULL REFERENCES production_orders(production_order_id) ON DELETE CASCADE,
    line_type               TEXT NOT NULL CHECK (line_type IN ('MATERIAL','LABOR','OVERHEAD')),
    product_id              INTEGER REFERENCES products(product_id),
    work_center_id          INTEGER REFERENCES work_centers(work_center_id),
    planned_quantity        NUMERIC NOT NULL,
    actual_quantity         NUMERIC DEFAULT 0,
    unit_cost               NUMERIC DEFAULT 0,
    total_cost              NUMERIC DEFAULT 0,
    notes                   TEXT,
    is_deleted              INTEGER NOT NULL DEFAULT 0
);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS number_sequences (
    sequence_id             INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    sequence_key            TEXT NOT NULL,
    sequence_prefix         TEXT,
    current_value           INTEGER NOT NULL DEFAULT 0,
    increment_by            INTEGER NOT NULL DEFAULT 1,
    padding_length          INTEGER NOT NULL DEFAULT 4,
    reset_yearly            INTEGER NOT NULL DEFAULT 0,
    last_reset_year         INTEGER,
    updated_at              TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (company_id, sequence_key)
);

CREATE TABLE IF NOT EXISTS system_settings (
    setting_id              INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    setting_key             TEXT NOT NULL,
    setting_value           TEXT,
    setting_type            TEXT DEFAULT 'STRING'
        CHECK (setting_type IN ('STRING','INT','DECIMAL','BOOL','DATE','JSON')),
    description             TEXT,
    updated_by              TEXT,
    updated_at              TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (company_id, setting_key)
);

CREATE TABLE IF NOT EXISTS audit_log (
    log_id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id              TEXT NOT NULL,
    user_id                 TEXT,
    action_type             TEXT NOT NULL
        CHECK (action_type IN ('INSERT','UPDATE','DELETE','LOGIN','LOGOUT','EXPORT','IMPORT','PRINT')),
    table_name              TEXT,
    record_id               TEXT,
    old_values              TEXT,
    new_values              TEXT,
    ip_address              TEXT,
    device_name             TEXT,
    action_date             TEXT NOT NULL DEFAULT (datetime('now')),
    notes                   TEXT
);

CREATE INDEX IF NOT EXISTS ix_audit_company_date ON audit_log(company_id, action_date DESC);
CREATE INDEX IF NOT EXISTS ix_audit_table ON audit_log(table_name, record_id);

-- ========================================================================
-- ========================================================================

CREATE VIEW IF NOT EXISTS v_products_with_class AS
SELECT
    p.product_id,
    p.company_id,
    p.product_code,
    p.product_name_ar,
    p.product_name_en,
    p.barcode,
    p.class_id,
    pc.class_code,
    pc.class_name_ar AS class_name,
    p.cost_price,
    p.price_list,
    p.is_active,
    p.is_stock_item
FROM products p
LEFT JOIN product_classes pc ON p.class_id = pc.class_id
WHERE p.is_deleted = 0;

CREATE VIEW IF NOT EXISTS v_stock_summary AS
SELECT
    sb.company_id,
    sb.warehouse_id,
    w.warehouse_name_ar,
    sb.product_id,
    p.product_code,
    p.product_name_ar,
    sb.available_qty,
    sb.reserved_qty,
    (sb.available_qty - sb.reserved_qty) AS free_qty,
    sb.average_cost,
    (sb.available_qty * COALESCE(sb.average_cost, 0)) AS stock_value
FROM stock_balances sb
INNER JOIN warehouses w ON sb.warehouse_id = w.warehouse_id
INNER JOIN products p ON sb.product_id = p.product_id
WHERE sb.is_deleted = 0;

CREATE VIEW IF NOT EXISTS v_low_stock AS
SELECT
    p.product_id,
    p.product_code,
    p.product_name_ar,
    p.reorder_level,
    p.safety_stock,
    COALESCE(SUM(sb.available_qty), 0) AS total_available,
    CASE
        WHEN COALESCE(SUM(sb.available_qty), 0) = 0 THEN 'OUT_OF_STOCK'
        WHEN COALESCE(SUM(sb.available_qty), 0) <= p.reorder_level THEN 'REORDER'
        ELSE 'OK'
    END AS stock_status
FROM products p
LEFT JOIN stock_balances sb ON p.product_id = sb.product_id AND sb.is_deleted = 0
WHERE p.is_deleted = 0 AND p.is_stock_item = 1
GROUP BY p.product_id, p.product_code, p.product_name_ar, p.reorder_level, p.safety_stock;

-- ========================================================================
-- ========================================================================


COMMIT;

-- ============================================================
-- source: archive/legacy_schema_snapshots/pg.sql
-- ============================================================
-- ========================================================================
-- FILE: dba/pg.sql
-- PROJECT: SHOUTECH ERP V10 — PostgreSQL Full Schema
-- VERSION: 10.10.0
-- ========================================================================

CREATE EXTENSION IF NOT EXISTS "pgcrypto";

SET client_encoding = 'UTF8';
SET timezone = 'UTC';

BEGIN;

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS companies (
    company_id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code            VARCHAR(50) NOT NULL UNIQUE,
    company_name_ar         VARCHAR(200) NOT NULL,
    company_name_en         VARCHAR(200),
    legal_name              VARCHAR(300),
    tax_number              VARCHAR(50),
    commercial_register     VARCHAR(50),
    address_line1           VARCHAR(300),
    city                    VARCHAR(100),
    country                 VARCHAR(10) DEFAULT 'SA',
    phone                   VARCHAR(50),
    mobile                  VARCHAR(50),
    email                   VARCHAR(200),
    website                 VARCHAR(200),
    logo_path               VARCHAR(500),
    default_currency_code   VARCHAR(3) DEFAULT 'SAR',
    default_language        VARCHAR(10) DEFAULT 'ar',
    time_zone               VARCHAR(100) DEFAULT 'Asia/Riyadh',
    database_name           VARCHAR(100),
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    created_by              UUID,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_by              UUID,
    updated_at              TIMESTAMPTZ
);

CREATE TABLE IF NOT EXISTS branches (
    branch_id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id              UUID NOT NULL REFERENCES companies(company_id),
    branch_code             VARCHAR(50) NOT NULL,
    branch_name_ar          VARCHAR(200) NOT NULL,
    branch_name_en          VARCHAR(200),
    address_line1           VARCHAR(300),
    city                    VARCHAR(100),
    phone                   VARCHAR(50),
    manager_user_id         UUID,
    is_default              BOOLEAN NOT NULL DEFAULT FALSE,
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_branches_code UNIQUE (company_id, branch_code)
);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS users (
    user_id                 UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id              UUID NOT NULL REFERENCES companies(company_id),
    username                VARCHAR(100) NOT NULL,
    full_name_ar            VARCHAR(200) NOT NULL,
    full_name_en            VARCHAR(200),
    email                   VARCHAR(200),
    mobile                  VARCHAR(50),
    password_hash           VARCHAR(500),
    password_salt           VARCHAR(200),
    employee_id             BIGINT,
    default_branch_id       UUID REFERENCES branches(branch_id),
    is_system_admin         BOOLEAN NOT NULL DEFAULT FALSE,
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_locked               BOOLEAN NOT NULL DEFAULT FALSE,
    locked_reason           VARCHAR(500),
    last_login_at           TIMESTAMPTZ,
    failed_login_count      INT NOT NULL DEFAULT 0,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at              TIMESTAMPTZ,
    CONSTRAINT uq_users_username UNIQUE (company_id, username)
);

CREATE TABLE IF NOT EXISTS roles (
    role_id                 BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL REFERENCES companies(company_id),
    role_code               VARCHAR(50) NOT NULL,
    role_name_ar            VARCHAR(200) NOT NULL,
    role_name_en            VARCHAR(200),
    description             VARCHAR(500),
    is_system_role          BOOLEAN NOT NULL DEFAULT FALSE,
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_roles_code UNIQUE (company_id, role_code)
);

CREATE TABLE IF NOT EXISTS permissions (
    permission_id           BIGSERIAL PRIMARY KEY,
    permission_code         VARCHAR(100) NOT NULL UNIQUE,
    module_name             VARCHAR(50) NOT NULL,
    resource_name           VARCHAR(100),
    action_name             VARCHAR(50) NOT NULL,
    description_ar          VARCHAR(500),
    description_en          VARCHAR(500),
    is_active               BOOLEAN NOT NULL DEFAULT TRUE
);

CREATE TABLE IF NOT EXISTS user_roles (
    user_role_id            BIGSERIAL PRIMARY KEY,
    user_id                 UUID NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    role_id                 BIGINT NOT NULL REFERENCES roles(role_id) ON DELETE CASCADE,
    assigned_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_user_roles UNIQUE (user_id, role_id)
);

CREATE TABLE IF NOT EXISTS role_permissions (
    role_permission_id      BIGSERIAL PRIMARY KEY,
    role_id                 BIGINT NOT NULL REFERENCES roles(role_id) ON DELETE CASCADE,
    permission_id           BIGINT NOT NULL REFERENCES permissions(permission_id) ON DELETE CASCADE,
    granted                 BOOLEAN NOT NULL DEFAULT TRUE,
    granted_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_role_permissions UNIQUE (role_id, permission_id)
);

CREATE TABLE IF NOT EXISTS user_permissions (
    user_permission_id      BIGSERIAL PRIMARY KEY,
    user_id                 UUID NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    permission_id           BIGINT NOT NULL REFERENCES permissions(permission_id) ON DELETE CASCADE,
    granted                 BOOLEAN NOT NULL DEFAULT TRUE,
    granted_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_user_permissions UNIQUE (user_id, permission_id)
);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS currencies (
    currency_code           VARCHAR(3) PRIMARY KEY,
    currency_name_ar        VARCHAR(100) NOT NULL,
    currency_name_en        VARCHAR(100),
    symbol                  VARCHAR(10),
    decimal_places          SMALLINT NOT NULL DEFAULT 2,
    is_active               BOOLEAN NOT NULL DEFAULT TRUE
);

CREATE TABLE IF NOT EXISTS currency_rates (
    rate_id                 BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL REFERENCES companies(company_id),
    from_currency           VARCHAR(3) NOT NULL REFERENCES currencies(currency_code),
    to_currency             VARCHAR(3) NOT NULL REFERENCES currencies(currency_code),
    rate                    NUMERIC(18,6) NOT NULL,
    effective_date          DATE NOT NULL DEFAULT CURRENT_DATE,
    source                  VARCHAR(50),
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS tax_codes (
    tax_code_id             BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL REFERENCES companies(company_id),
    tax_code                VARCHAR(50) NOT NULL,
    tax_name_ar             VARCHAR(200) NOT NULL,
    tax_name_en             VARCHAR(200),
    tax_rate                NUMERIC(5,2) NOT NULL DEFAULT 0,
    tax_category            VARCHAR(20) NOT NULL DEFAULT 'STANDARD'
        CHECK (tax_category IN ('STANDARD','ZERO','EXEMPT')),
    zatca_item_code         VARCHAR(50),
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_tax_codes UNIQUE (company_id, tax_code)
);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS account_types (
    account_type_id         BIGSERIAL PRIMARY KEY,
    type_code               VARCHAR(20) NOT NULL UNIQUE,
    type_name_ar            VARCHAR(100) NOT NULL,
    type_name_en            VARCHAR(100) NOT NULL,
    normal_balance          VARCHAR(10) NOT NULL CHECK (normal_balance IN ('DEBIT','CREDIT')),
    sort_order              INT NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS chart_of_accounts (
    account_id              BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL REFERENCES companies(company_id),
    parent_account_id       BIGINT REFERENCES chart_of_accounts(account_id),
    account_code            VARCHAR(50) NOT NULL,
    account_name_ar         VARCHAR(200) NOT NULL,
    account_name_en         VARCHAR(200),
    account_type_id         BIGINT NOT NULL REFERENCES account_types(account_type_id),
    account_level           SMALLINT NOT NULL DEFAULT 1,
    is_leaf                 BOOLEAN NOT NULL DEFAULT TRUE,
    currency_code           VARCHAR(3),
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_coa_code UNIQUE (company_id, account_code)
);

CREATE TABLE IF NOT EXISTS fiscal_years (
    fiscal_year_id          BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL REFERENCES companies(company_id),
    year_code               VARCHAR(20) NOT NULL,
    start_date              DATE NOT NULL,
    end_date                DATE NOT NULL,
    status                  VARCHAR(20) NOT NULL DEFAULT 'OPEN'
        CHECK (status IN ('OPEN','CLOSING','CLOSED')),
    CONSTRAINT uq_fiscal_years UNIQUE (company_id, year_code)
);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS product_classes (
    class_id                BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL REFERENCES companies(company_id),
    parent_class_id         BIGINT REFERENCES product_classes(class_id),

    card_no                 BIGINT NOT NULL DEFAULT 0,

    class_code              VARCHAR(50) NOT NULL,
    class_name_ar           VARCHAR(200) NOT NULL,
    class_name_en           VARCHAR(200),
    class_level             SMALLINT NOT NULL DEFAULT 1,

    warehouse               VARCHAR(200),
    general_specs           VARCHAR(1000),
    notes                   VARCHAR(1000),
    is_sales_class          BOOLEAN NOT NULL DEFAULT FALSE,

    icon_path               VARCHAR(500),
    image_path              VARCHAR(500),

    default_account_id      BIGINT REFERENCES chart_of_accounts(account_id),
    default_tax_code_id     BIGINT REFERENCES tax_codes(tax_code_id),

    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    deleted_at              TIMESTAMPTZ,

    created_by              UUID,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_by              UUID,
    updated_at              TIMESTAMPTZ,

    CONSTRAINT uq_pcls_code   UNIQUE (company_id, class_code),
    CONSTRAINT uq_pcls_cardno UNIQUE (company_id, card_no)
);

CREATE INDEX IF NOT EXISTS ix_pcls_company ON product_classes(company_id, is_active) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_pcls_parent  ON product_classes(parent_class_id) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_pcls_sales   ON product_classes(company_id, is_sales_class) WHERE is_deleted = FALSE AND is_sales_class = TRUE;

CREATE TABLE IF NOT EXISTS product_class_sequences (
    company_id              UUID PRIMARY KEY REFERENCES companies(company_id),
    last_card_no            BIGINT NOT NULL DEFAULT 0,
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS product_class_attributes (
    attribute_id            BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    class_id                BIGINT NOT NULL REFERENCES product_classes(class_id) ON DELETE CASCADE,
    attribute_key           VARCHAR(100) NOT NULL,
    attribute_value         VARCHAR(500),
    attribute_group         VARCHAR(50),
    sort_order              INT NOT NULL DEFAULT 0,
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS product_class_images (
    image_id                BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    class_id                BIGINT NOT NULL REFERENCES product_classes(class_id) ON DELETE CASCADE,
    image_path              VARCHAR(500) NOT NULL,
    image_order             SMALLINT NOT NULL DEFAULT 0,
    is_primary              BOOLEAN NOT NULL DEFAULT FALSE,
    title                   VARCHAR(200),
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS brands (
    brand_id                BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL REFERENCES companies(company_id),
    brand_code              VARCHAR(50) NOT NULL,
    brand_name_ar           VARCHAR(200) NOT NULL,
    brand_name_en           VARCHAR(200),
    description             VARCHAR(500),
    logo_path               VARCHAR(500),
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_brands_code UNIQUE (company_id, brand_code)
);

CREATE TABLE IF NOT EXISTS units_of_measure (
    uom_id                  BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL REFERENCES companies(company_id),
    uom_code                VARCHAR(20) NOT NULL,
    uom_name_ar             VARCHAR(50) NOT NULL,
    uom_name_en             VARCHAR(50),
    base_uom_id             BIGINT REFERENCES units_of_measure(uom_id),
    conversion_factor       NUMERIC(18,6) NOT NULL DEFAULT 1,
    is_base_unit            BOOLEAN NOT NULL DEFAULT FALSE,
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_uom_code UNIQUE (company_id, uom_code)
);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS products (
    product_id              BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL REFERENCES companies(company_id),

    product_code            VARCHAR(50) NOT NULL,
    product_name_ar         VARCHAR(255) NOT NULL,
    product_name_en         VARCHAR(255),
    short_name              VARCHAR(100),
    search_name             VARCHAR(255),
    description             TEXT,
    notes                   TEXT,

    class_id                BIGINT REFERENCES product_classes(class_id),
    brand_id                BIGINT REFERENCES brands(brand_id),

    primary_uom_id          BIGINT REFERENCES units_of_measure(uom_id),

    barcode                 VARCHAR(50),
    quick_code              VARCHAR(20),
    sku                     VARCHAR(50),
    gtin                    VARCHAR(50),
    alt_code                VARCHAR(50),
    old_code                VARCHAR(50),
    supplier_code           VARCHAR(50),

    cost_price              NUMERIC(18,6) NOT NULL DEFAULT 0,
    price_list              NUMERIC(18,6) NOT NULL DEFAULT 0,
    price_retail            NUMERIC(18,6) NOT NULL DEFAULT 0,
    price_wholesale         NUMERIC(18,6) NOT NULL DEFAULT 0,
    price_special           NUMERIC(18,6) NOT NULL DEFAULT 0,
    price_export            NUMERIC(18,6) NOT NULL DEFAULT 0,
    price_distributor       NUMERIC(18,6) NOT NULL DEFAULT 0,
    price_currency          VARCHAR(3) DEFAULT 'SAR',

    tax_rate                NUMERIC(5,2) NOT NULL DEFAULT 0,
    is_taxable              BOOLEAN NOT NULL DEFAULT FALSE,
    price_includes_tax      BOOLEAN NOT NULL DEFAULT FALSE,

    is_stock_item           BOOLEAN NOT NULL DEFAULT TRUE,
    is_service_item         BOOLEAN NOT NULL DEFAULT FALSE,
    is_pos_item             BOOLEAN NOT NULL DEFAULT TRUE,
    is_serialized           BOOLEAN NOT NULL DEFAULT FALSE,
    is_batch_tracked        BOOLEAN NOT NULL DEFAULT FALSE,
    is_expiry_tracked       BOOLEAN NOT NULL DEFAULT FALSE,
    item_type               VARCHAR(20) NOT NULL DEFAULT 'Stock',

    reorder_level           NUMERIC(18,3) NOT NULL DEFAULT 0,
    safety_stock            NUMERIC(18,3) NOT NULL DEFAULT 0,
    min_qty                 NUMERIC(18,3) NOT NULL DEFAULT 0,
    max_qty                 NUMERIC(18,3) NOT NULL DEFAULT 0,

    weight_net              NUMERIC(18,6),
    weight_gross            NUMERIC(18,6),
    weight_unit             VARCHAR(20),
    length                  NUMERIC(18,6),
    width                   NUMERIC(18,6),
    height                  NUMERIC(18,6),
    dim_unit                VARCHAR(20),
    volume                  NUMERIC(18,6),
    volume_unit             VARCHAR(20),
    area                    NUMERIC(18,6),
    area_unit               VARCHAR(20),
    pack_qty                NUMERIC(18,6),
    units_per_parcel        NUMERIC(18,6),

    main_image_path         VARCHAR(500),

    origin_country          VARCHAR(100),
    manufacturer            VARCHAR(200),
    model                   VARCHAR(100),
    color                   VARCHAR(50),
    size_spec               VARCHAR(100),
    grade                   VARCHAR(50),
    tags                    VARCHAR(500),

    acc_inventory           VARCHAR(50),
    acc_sales               VARCHAR(50),
    acc_sales_return        VARCHAR(50),
    acc_cogs                VARCHAR(50),
    acc_purchase            VARCHAR(50),
    acc_purchase_return     VARCHAR(50),
    acc_expense             VARCHAR(50),
    acc_wip                 VARCHAR(50),

    allow_sale              BOOLEAN NOT NULL DEFAULT TRUE,
    allow_purchase          BOOLEAN NOT NULL DEFAULT TRUE,
    allow_return            BOOLEAN NOT NULL DEFAULT TRUE,
    allow_negative          BOOLEAN NOT NULL DEFAULT FALSE,
    sales_blocked           BOOLEAN NOT NULL DEFAULT FALSE,
    track_location          BOOLEAN NOT NULL DEFAULT FALSE,
    quality_check_required  BOOLEAN NOT NULL DEFAULT FALSE,

    cost_method             VARCHAR(20) NOT NULL DEFAULT 'WeightedAvg',
    cost_currency           VARCHAR(3),

    default_warehouse       VARCHAR(200),
    shelf_bin               VARCHAR(50),
    lead_time_days          INT NOT NULL DEFAULT 0,

    units_json              JSONB,
    prices_json             JSONB,
    attributes_json         JSONB,

    status                  VARCHAR(20) NOT NULL DEFAULT 'Active',
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,

    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    deleted_at              TIMESTAMPTZ,
    created_by              UUID,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_by              UUID,
    updated_at              TIMESTAMPTZ,

    CONSTRAINT uq_products_code UNIQUE (company_id, product_code)
);

CREATE INDEX IF NOT EXISTS ix_prod_company ON products(company_id, is_active) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_prod_barcode ON products(company_id, barcode) WHERE barcode IS NOT NULL AND is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_prod_class   ON products(class_id, is_active) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_prod_brand   ON products(brand_id, is_active) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS product_units (
    product_unit_id         BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    product_id              BIGINT NOT NULL REFERENCES products(product_id) ON DELETE CASCADE,
    uom_id                  BIGINT REFERENCES units_of_measure(uom_id),
    unit_name               VARCHAR(50) NOT NULL,
    factor                  NUMERIC(18,6) NOT NULL DEFAULT 1,
    barcode                 VARCHAR(50),
    line_no                 SMALLINT NOT NULL DEFAULT 1,
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE
);

CREATE TABLE IF NOT EXISTS product_barcodes (
    barcode_id              BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    product_id              BIGINT NOT NULL REFERENCES products(product_id) ON DELETE CASCADE,
    barcode                 VARCHAR(50) NOT NULL,
    barcode_type            VARCHAR(20) NOT NULL DEFAULT 'EAN',
    unit_name               VARCHAR(50),
    is_primary              BOOLEAN NOT NULL DEFAULT FALSE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    CONSTRAINT uq_prod_barcodes UNIQUE (company_id, barcode)
);

CREATE TABLE IF NOT EXISTS product_suppliers (
    product_supplier_id     BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    product_id              BIGINT NOT NULL REFERENCES products(product_id) ON DELETE CASCADE,
    supplier_code           VARCHAR(50) NOT NULL,
    supplier_item_code      VARCHAR(100),
    lead_time_days          INT NOT NULL DEFAULT 0,
    min_order_qty           NUMERIC(18,6) NOT NULL DEFAULT 0,
    is_preferred            BOOLEAN NOT NULL DEFAULT FALSE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE
);

CREATE TABLE IF NOT EXISTS product_attributes (
    attribute_id            BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    product_id              BIGINT NOT NULL REFERENCES products(product_id) ON DELETE CASCADE,
    attribute_key           VARCHAR(100) NOT NULL,
    attribute_value         VARCHAR(500),
    attribute_group         VARCHAR(50),
    sort_order              INT NOT NULL DEFAULT 0,
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    CONSTRAINT uq_product_attributes UNIQUE (company_id, product_id, attribute_key)
);

CREATE TABLE IF NOT EXISTS product_images (
    image_id                BIGSERIAL PRIMARY KEY,
    product_id              BIGINT NOT NULL REFERENCES products(product_id) ON DELETE CASCADE,
    image_path              VARCHAR(500) NOT NULL,
    image_order             SMALLINT NOT NULL DEFAULT 0,
    is_primary              BOOLEAN NOT NULL DEFAULT FALSE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS warehouse_types (
    warehouse_type_id       BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL REFERENCES companies(company_id),
    type_code               VARCHAR(50) NOT NULL,
    type_name_ar            VARCHAR(100) NOT NULL,
    type_name_en            VARCHAR(100),
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    CONSTRAINT uq_wh_types_code UNIQUE (company_id, type_code)
);

CREATE TABLE IF NOT EXISTS warehouses (
    warehouse_id            BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL REFERENCES companies(company_id),
    branch_id               UUID REFERENCES branches(branch_id),
    warehouse_type_id       BIGINT REFERENCES warehouse_types(warehouse_type_id),

    card_no                 BIGINT NOT NULL DEFAULT 0,

    warehouse_code          VARCHAR(50) NOT NULL,
    warehouse_name_ar       VARCHAR(200) NOT NULL,
    warehouse_name_en       VARCHAR(200),

    parent_code             VARCHAR(50),

    address_line1           VARCHAR(300),
    city                    VARCHAR(100),
    region                  VARCHAR(100),
    country                 VARCHAR(10) DEFAULT 'SA',
    postal_code             VARCHAR(20),

    phone                   VARCHAR(50),
    mobile                  VARCHAR(50),
    email                   VARCHAR(100),

    aisle                   VARCHAR(50),
    rack                    VARCHAR(50),
    shelf                   VARCHAR(50),

    notes                   TEXT,

    is_default              BOOLEAN NOT NULL DEFAULT FALSE,
    allow_negative_stock    BOOLEAN NOT NULL DEFAULT FALSE,
    auto_reserve_on_receipt BOOLEAN NOT NULL DEFAULT TRUE,

    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_frozen               BOOLEAN NOT NULL DEFAULT FALSE,

    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    created_by              UUID,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at              TIMESTAMPTZ,
    updated_by              UUID,

    CONSTRAINT uq_warehouses_code   UNIQUE (company_id, warehouse_code),
    CONSTRAINT uq_warehouses_cardno UNIQUE (company_id, card_no)
);

CREATE INDEX IF NOT EXISTS ix_wh_company ON warehouses(company_id, is_active) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_wh_default ON warehouses(company_id, is_default) WHERE is_deleted = FALSE AND is_default = TRUE;
CREATE INDEX IF NOT EXISTS ix_wh_parent  ON warehouses(parent_code) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS warehouse_sequences (
    company_id   UUID PRIMARY KEY REFERENCES companies(company_id),
    last_card_no BIGINT NOT NULL DEFAULT 0,
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS warehouse_zones (
    zone_id                 BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    warehouse_id            BIGINT NOT NULL REFERENCES warehouses(warehouse_id) ON DELETE CASCADE,
    zone_code               VARCHAR(50) NOT NULL,
    zone_name_ar            VARCHAR(200) NOT NULL,
    zone_name_en            VARCHAR(200),
    zone_type               VARCHAR(30) NOT NULL
        CHECK (zone_type IN ('RECEIVING','STORAGE','PICKING','PACKING','SHIPPING','QUARANTINE','DAMAGED')),
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    CONSTRAINT uq_wh_zones_code UNIQUE (warehouse_id, zone_code)
);

CREATE TABLE IF NOT EXISTS warehouse_locations (
    location_id             BIGSERIAL PRIMARY KEY,
    warehouse_id            BIGINT NOT NULL REFERENCES warehouses(warehouse_id) ON DELETE CASCADE,
    zone_id                 BIGINT REFERENCES warehouse_zones(zone_id),
    location_code           VARCHAR(50) NOT NULL,
    location_name           VARCHAR(200),
    aisle                   VARCHAR(50),
    rack                    VARCHAR(50),
    shelf                   VARCHAR(50),
    bin                     VARCHAR(50),
    location_type           VARCHAR(20) NOT NULL DEFAULT 'STORAGE'
        CHECK (location_type IN ('STORAGE','PICKING','BULK','DAMAGED','QUARANTINE')),
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_blocked              BOOLEAN NOT NULL DEFAULT FALSE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    CONSTRAINT uq_wh_locations_code UNIQUE (warehouse_id, location_code)
);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS stock_balances (
    stock_balance_id        BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    warehouse_id            BIGINT NOT NULL REFERENCES warehouses(warehouse_id),
    location_id             BIGINT REFERENCES warehouse_locations(location_id),
    product_id              BIGINT NOT NULL REFERENCES products(product_id),

    available_qty           NUMERIC(18,4) NOT NULL DEFAULT 0,
    reserved_qty            NUMERIC(18,4) NOT NULL DEFAULT 0,
    damaged_qty             NUMERIC(18,4) NOT NULL DEFAULT 0,
    on_order_qty            NUMERIC(18,4) NOT NULL DEFAULT 0,

    average_cost            NUMERIC(18,6),
    last_cost               NUMERIC(18,6),

    last_updated            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,

    CONSTRAINT uq_stock_balances UNIQUE (company_id, warehouse_id, location_id, product_id)
);

CREATE INDEX IF NOT EXISTS ix_sb_wh_prod  ON stock_balances(warehouse_id, product_id) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_sb_available ON stock_balances(available_qty) WHERE available_qty > 0 AND is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS stock_movements (
    movement_id             BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    movement_type           VARCHAR(30) NOT NULL
        CHECK (movement_type IN ('PURCHASE','SALE','RETURN_PURCHASE','RETURN_SALE',
                                 'TRANSFER_IN','TRANSFER_OUT','ADJUSTMENT',
                                 'COUNT_OPEN','COUNT_CLOSE','REVERSAL')),
    movement_date           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    warehouse_id_from       BIGINT REFERENCES warehouses(warehouse_id),
    warehouse_id_to         BIGINT REFERENCES warehouses(warehouse_id),
    product_id              BIGINT NOT NULL REFERENCES products(product_id),
    quantity                NUMERIC(18,4) NOT NULL,
    unit_cost               NUMERIC(18,6),
    total_cost              NUMERIC(18,6),
    batch_number            VARCHAR(50),
    serial_number           VARCHAR(100),
    reference_table         VARCHAR(50),
    reference_id            BIGINT,
    reference_number        VARCHAR(50),
    notes                   TEXT,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    created_by              UUID,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_sm_product ON stock_movements(product_id, movement_date DESC) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_sm_ref     ON stock_movements(reference_table, reference_id) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS material_stock_balances (
    stock_balance_id        BIGSERIAL PRIMARY KEY,
    company_id              TEXT NOT NULL,
    warehouse_id            BIGINT NOT NULL,
    material_id             BIGINT NOT NULL,
    available_qty           NUMERIC(18,4) NOT NULL DEFAULT 0,
    reserved_qty            NUMERIC(18,4) NOT NULL DEFAULT 0,
    average_cost            NUMERIC(18,6) NOT NULL DEFAULT 0,
    last_cost               NUMERIC(18,6) NOT NULL DEFAULT 0,
    last_updated            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    UNIQUE (company_id, warehouse_id, material_id)
);

CREATE INDEX IF NOT EXISTS ix_material_stock_balances_wh_mat
    ON material_stock_balances(company_id, warehouse_id, material_id)
    WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS material_stock_movements (
    stock_movement_id       BIGSERIAL PRIMARY KEY,
    company_id              TEXT NOT NULL,
    warehouse_id            BIGINT NOT NULL,
    material_id             BIGINT NOT NULL,
    movement_date           TIMESTAMPTZ NOT NULL,
    movement_type           VARCHAR(30) NOT NULL,
    document_no             TEXT NOT NULL,
    qty_in                  NUMERIC(18,4) NOT NULL DEFAULT 0,
    qty_out                 NUMERIC(18,4) NOT NULL DEFAULT 0,
    unit_cost               NUMERIC(18,6) NOT NULL DEFAULT 0,
    note                    TEXT,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE
);

CREATE INDEX IF NOT EXISTS ix_material_stock_movements_wh_mat_date
    ON material_stock_movements(company_id, warehouse_id, material_id, movement_date);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS batches (
    batch_id                BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    product_id              BIGINT NOT NULL REFERENCES products(product_id),
    warehouse_id            BIGINT NOT NULL REFERENCES warehouses(warehouse_id),
    location_id             BIGINT REFERENCES warehouse_locations(location_id),
    batch_number            VARCHAR(50) NOT NULL,
    supplier_batch_number   VARCHAR(50),
    manufacturing_date      DATE,
    expiry_date             DATE,
    received_date           DATE NOT NULL DEFAULT CURRENT_DATE,
    original_quantity       NUMERIC(18,4) NOT NULL DEFAULT 0,
    current_quantity        NUMERIC(18,4) NOT NULL DEFAULT 0,
    reserved_quantity       NUMERIC(18,4) NOT NULL DEFAULT 0,
    purchase_cost           NUMERIC(18,6),
    average_cost            NUMERIC(18,6),
    supplier_invoice_number VARCHAR(50),
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_fully_consumed       BOOLEAN NOT NULL DEFAULT FALSE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    created_by              UUID,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at              TIMESTAMPTZ,
    CONSTRAINT uq_batches_number UNIQUE (company_id, product_id, batch_number)
);

CREATE INDEX IF NOT EXISTS ix_batch_expiry  ON batches(expiry_date) WHERE is_active = TRUE AND is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_batch_product ON batches(product_id, warehouse_id) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS serial_numbers (
    serial_id               BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    product_id              BIGINT NOT NULL REFERENCES products(product_id),
    warehouse_id            BIGINT NOT NULL REFERENCES warehouses(warehouse_id),
    location_id             BIGINT REFERENCES warehouse_locations(location_id),
    batch_id                BIGINT REFERENCES batches(batch_id),
    serial_number           VARCHAR(100) NOT NULL,
    status                  VARCHAR(20) NOT NULL DEFAULT 'IN_STOCK'
        CHECK (status IN ('IN_STOCK','RESERVED','SOLD','RETURNED','DAMAGED','LOST')),
    received_date           DATE NOT NULL DEFAULT CURRENT_DATE,
    sold_date               DATE,
    customer_id             BIGINT,
    purchase_invoice_id     BIGINT,
    sales_invoice_id        BIGINT,
    last_transaction_date   TIMESTAMPTZ,
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    created_by              UUID,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_serial_numbers UNIQUE (company_id, product_id, serial_number)
);

CREATE INDEX IF NOT EXISTS ix_serial_status ON serial_numbers(status, product_id) WHERE is_active = TRUE AND is_deleted = FALSE;

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS valuation_methods (
    valuation_method_id     BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    method_code             VARCHAR(20) NOT NULL,
    method_name_ar          VARCHAR(100) NOT NULL,
    method_name_en          VARCHAR(100) NOT NULL,
    method_description      VARCHAR(500),
    is_default              BOOLEAN NOT NULL DEFAULT FALSE,
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    CONSTRAINT uq_val_methods UNIQUE (company_id, method_code)
);

CREATE TABLE IF NOT EXISTS product_valuation (
    product_valuation_id    BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    product_id              BIGINT NOT NULL REFERENCES products(product_id),
    valuation_method_id     BIGINT NOT NULL REFERENCES valuation_methods(valuation_method_id),
    effective_date          DATE NOT NULL DEFAULT CURRENT_DATE,
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    CONSTRAINT uq_prod_valuation UNIQUE (company_id, product_id, effective_date)
);

CREATE TABLE IF NOT EXISTS cost_layers (
    layer_id                BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    product_id              BIGINT NOT NULL REFERENCES products(product_id),
    warehouse_id            BIGINT NOT NULL REFERENCES warehouses(warehouse_id),
    batch_id                BIGINT REFERENCES batches(batch_id),
    layer_date              DATE NOT NULL,
    layer_sequence          INT NOT NULL DEFAULT 0,
    quantity                NUMERIC(18,4) NOT NULL DEFAULT 0,
    unit_cost               NUMERIC(18,6) NOT NULL DEFAULT 0,
    remaining_quantity      NUMERIC(18,4) NOT NULL DEFAULT 0,
    currency_code           VARCHAR(3) DEFAULT 'SAR',
    source_reference        VARCHAR(50),
    source_id               BIGINT,
    is_consumed             BOOLEAN NOT NULL DEFAULT FALSE,
    consumed_at             TIMESTAMPTZ,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    created_by              UUID,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_layers_active ON cost_layers(product_id, warehouse_id, layer_date)
    WHERE is_consumed = FALSE AND is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS valuation_history (
    valuation_history_id    BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    product_id              BIGINT NOT NULL REFERENCES products(product_id),
    warehouse_id            BIGINT NOT NULL REFERENCES warehouses(warehouse_id),
    valuation_date          DATE NOT NULL,
    valuation_method_id     BIGINT REFERENCES valuation_methods(valuation_method_id),
    total_quantity          NUMERIC(18,4) NOT NULL DEFAULT 0,
    total_value             NUMERIC(18,6) NOT NULL DEFAULT 0,
    average_cost            NUMERIC(18,6) NOT NULL DEFAULT 0,
    currency_code           VARCHAR(3) DEFAULT 'SAR',
    notes                   TEXT,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_val_hist ON valuation_history(product_id, valuation_date DESC) WHERE is_deleted = FALSE;

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS stock_reservations (
    reservation_id          BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    stock_balance_id        BIGINT NOT NULL REFERENCES stock_balances(stock_balance_id) ON DELETE CASCADE,
    batch_id                BIGINT REFERENCES batches(batch_id),
    serial_id               BIGINT REFERENCES serial_numbers(serial_id),
    reference_table         VARCHAR(50) NOT NULL,
    reference_id            BIGINT NOT NULL,
    reference_line_id       BIGINT,
    reference_number        VARCHAR(50),
    reserved_quantity       NUMERIC(18,4) NOT NULL DEFAULT 0,
    original_quantity       NUMERIC(18,4) NOT NULL DEFAULT 0,
    released_quantity       NUMERIC(18,4) NOT NULL DEFAULT 0,
    reserved_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    reserved_by             UUID,
    released_at             TIMESTAMPTZ,
    released_by             UUID,
    status                  VARCHAR(20) NOT NULL DEFAULT 'ACTIVE'
        CHECK (status IN ('ACTIVE','PARTIAL','RELEASED','EXPIRED')),
    expiry_date             DATE,
    notes                   TEXT,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_reservations_ref ON stock_reservations(reference_table, reference_id, status)
    WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS stock_adjustments (
    adjustment_id           BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    adjustment_number       VARCHAR(50) NOT NULL,
    adjustment_date         DATE NOT NULL DEFAULT CURRENT_DATE,
    warehouse_id            BIGINT NOT NULL REFERENCES warehouses(warehouse_id),
    adjustment_type         VARCHAR(20) NOT NULL
        CHECK (adjustment_type IN ('INCREASE','DECREASE','CORRECTION')),
    reason                  VARCHAR(200) NOT NULL,
    total_adjustment        NUMERIC(18,4) NOT NULL DEFAULT 0,
    status                  VARCHAR(20) NOT NULL DEFAULT 'DRAFT'
        CHECK (status IN ('DRAFT','POSTED','CANCELLED')),
    posted_at               TIMESTAMPTZ,
    posted_by               UUID,
    notes                   TEXT,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    created_by              UUID,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_adj_number UNIQUE (company_id, adjustment_number)
);

CREATE TABLE IF NOT EXISTS stock_adjustment_items (
    adjustment_item_id      BIGSERIAL PRIMARY KEY,
    adjustment_id           BIGINT NOT NULL REFERENCES stock_adjustments(adjustment_id) ON DELETE CASCADE,
    product_id              BIGINT NOT NULL REFERENCES products(product_id),
    batch_id                BIGINT REFERENCES batches(batch_id),
    location_id             BIGINT REFERENCES warehouse_locations(location_id),
    old_quantity            NUMERIC(18,4) NOT NULL DEFAULT 0,
    new_quantity            NUMERIC(18,4) NOT NULL DEFAULT 0,
    difference_quantity     NUMERIC(18,4) NOT NULL DEFAULT 0,
    unit_cost               NUMERIC(18,6),
    notes                   VARCHAR(200),
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE
);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS pos_terminals (
    terminal_id             BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    branch_id               UUID REFERENCES branches(branch_id),
    terminal_code           VARCHAR(50) NOT NULL,
    terminal_name_ar        VARCHAR(200) NOT NULL,
    terminal_name_en        VARCHAR(200),
    location                VARCHAR(200),
    warehouse_id            BIGINT REFERENCES warehouses(warehouse_id),
    cash_account_code       VARCHAR(50),
    receipt_printer         VARCHAR(100),
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    CONSTRAINT uq_pos_terminals UNIQUE (company_id, terminal_code)
);

CREATE TABLE IF NOT EXISTS pos_sessions (
    session_id              BIGSERIAL PRIMARY KEY,
    terminal_id             BIGINT NOT NULL REFERENCES pos_terminals(terminal_id),
    opened_by               UUID NOT NULL,
    opened_at               TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    closed_by               UUID,
    closed_at               TIMESTAMPTZ,
    opening_balance         NUMERIC(18,4) NOT NULL DEFAULT 0,
    expected_closing        NUMERIC(18,4),
    actual_closing          NUMERIC(18,4),
    total_sales             NUMERIC(18,4) DEFAULT 0,
    total_returns           NUMERIC(18,4) DEFAULT 0,
    total_cash              NUMERIC(18,4) DEFAULT 0,
    total_card              NUMERIC(18,4) DEFAULT 0,
    total_credit            NUMERIC(18,4) DEFAULT 0,
    total_discount          NUMERIC(18,4) DEFAULT 0,
    status                  VARCHAR(20) NOT NULL DEFAULT 'OPEN'
        CHECK (status IN ('OPEN','CLOSED','SUSPENDED','RECONCILED')),
    notes                   TEXT,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE
);

CREATE INDEX IF NOT EXISTS ix_pos_sessions ON pos_sessions(terminal_id, status) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS pos_transactions (
    pos_transaction_id      BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    terminal_id             BIGINT NOT NULL REFERENCES pos_terminals(terminal_id),
    session_id              BIGINT NOT NULL REFERENCES pos_sessions(session_id),
    invoice_id              BIGINT,
    transaction_type        VARCHAR(20) NOT NULL
        CHECK (transaction_type IN ('SALE','RETURN','VOID','QUOTATION')),
    transaction_number      VARCHAR(50) NOT NULL,
    customer_id             BIGINT,
    total_amount            NUMERIC(18,4) NOT NULL DEFAULT 0,
    total_discount          NUMERIC(18,4) DEFAULT 0,
    net_amount              NUMERIC(18,4) NOT NULL DEFAULT 0,
    paid_amount             NUMERIC(18,4) NOT NULL DEFAULT 0,
    change_amount           NUMERIC(18,4) DEFAULT 0,
    status                  VARCHAR(20) DEFAULT 'COMPLETED',
    is_deleted              BOOLEAN DEFAULT FALSE,
    created_by              UUID,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS pos_transaction_items (
    pos_transaction_item_id BIGSERIAL PRIMARY KEY,
    pos_transaction_id      BIGINT NOT NULL REFERENCES pos_transactions(pos_transaction_id) ON DELETE CASCADE,
    product_id              BIGINT NOT NULL REFERENCES products(product_id),
    batch_id                BIGINT REFERENCES batches(batch_id),
    serial_number_id        BIGINT REFERENCES serial_numbers(serial_id),
    quantity                NUMERIC(18,4) NOT NULL DEFAULT 0,
    unit_price              NUMERIC(18,6) NOT NULL DEFAULT 0,
    discount                NUMERIC(18,4) DEFAULT 0,
    total_amount            NUMERIC(18,4) NOT NULL DEFAULT 0,
    is_deleted              BOOLEAN DEFAULT FALSE
);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS customer_groups (
    group_id                BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    group_code              VARCHAR(50) NOT NULL,
    group_name_ar           VARCHAR(200) NOT NULL,
    group_name_en           VARCHAR(200),
    discount_percentage     NUMERIC(5,2) DEFAULT 0,
    price_list_id           BIGINT,
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    CONSTRAINT uq_cust_groups UNIQUE (company_id, group_code)
);

CREATE TABLE IF NOT EXISTS customers (
    customer_id             BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    customer_code           VARCHAR(50) NOT NULL,
    customer_name_ar        VARCHAR(200) NOT NULL,
    customer_name_en        VARCHAR(200),
    customer_type           VARCHAR(20) NOT NULL DEFAULT 'INDIVIDUAL'
        CHECK (customer_type IN ('INDIVIDUAL','COMPANY','GOVERNMENT')),
    group_id                BIGINT REFERENCES customer_groups(group_id),
    tax_number              VARCHAR(50),
    commercial_register     VARCHAR(50),
    credit_limit            NUMERIC(18,4) DEFAULT 0,
    credit_days             INT DEFAULT 0,
    current_balance         NUMERIC(18,4) DEFAULT 0,
    opening_balance         NUMERIC(18,4) DEFAULT 0,
    sales_rep_id            BIGINT,
    default_price_list_id   BIGINT,
    default_currency        VARCHAR(3) DEFAULT 'SAR',
    is_blocked              BOOLEAN NOT NULL DEFAULT FALSE,
    block_reason            VARCHAR(200),
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    created_by              UUID,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at              TIMESTAMPTZ,
    CONSTRAINT uq_customers_code UNIQUE (company_id, customer_code)
);

CREATE INDEX IF NOT EXISTS ix_cust_company ON customers(company_id, is_active) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS customer_contacts (
    contact_id              BIGSERIAL PRIMARY KEY,
    customer_id             BIGINT NOT NULL REFERENCES customers(customer_id) ON DELETE CASCADE,
    contact_name            VARCHAR(200) NOT NULL,
    job_title               VARCHAR(100),
    phone                   VARCHAR(50),
    mobile                  VARCHAR(50),
    email                   VARCHAR(200),
    is_primary              BOOLEAN NOT NULL DEFAULT FALSE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE
);

CREATE TABLE IF NOT EXISTS customer_addresses (
    address_id              BIGSERIAL PRIMARY KEY,
    customer_id             BIGINT NOT NULL REFERENCES customers(customer_id) ON DELETE CASCADE,
    address_type            VARCHAR(20) NOT NULL DEFAULT 'BILLING'
        CHECK (address_type IN ('BILLING','SHIPPING','OTHER')),
    address_line1           VARCHAR(300) NOT NULL,
    city                    VARCHAR(100),
    country                 VARCHAR(10) DEFAULT 'SA',
    postal_code             VARCHAR(20),
    is_default              BOOLEAN NOT NULL DEFAULT FALSE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE
);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS supplier_groups (
    group_id                BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    group_code              VARCHAR(50) NOT NULL,
    group_name_ar           VARCHAR(200) NOT NULL,
    group_name_en           VARCHAR(200),
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    CONSTRAINT uq_supp_groups UNIQUE (company_id, group_code)
);

CREATE TABLE IF NOT EXISTS suppliers (
    supplier_id             BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    supplier_code           VARCHAR(50) NOT NULL,
    supplier_name_ar        VARCHAR(200) NOT NULL,
    supplier_name_en        VARCHAR(200),
    group_id                BIGINT REFERENCES supplier_groups(group_id),
    tax_number              VARCHAR(50),
    commercial_register     VARCHAR(50),
    payment_terms_days      INT DEFAULT 0,
    credit_limit            NUMERIC(18,4) DEFAULT 0,
    current_balance         NUMERIC(18,4) DEFAULT 0,
    opening_balance         NUMERIC(18,4) DEFAULT 0,
    default_currency        VARCHAR(3) DEFAULT 'SAR',
    is_blocked              BOOLEAN NOT NULL DEFAULT FALSE,
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    created_by              UUID,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at              TIMESTAMPTZ,
    CONSTRAINT uq_suppliers_code UNIQUE (company_id, supplier_code)
);

CREATE INDEX IF NOT EXISTS ix_supp_company ON suppliers(company_id, is_active) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS supplier_contacts (
    contact_id              BIGSERIAL PRIMARY KEY,
    supplier_id             BIGINT NOT NULL REFERENCES suppliers(supplier_id) ON DELETE CASCADE,
    contact_name            VARCHAR(200) NOT NULL,
    job_title               VARCHAR(100),
    phone                   VARCHAR(50),
    mobile                  VARCHAR(50),
    email                   VARCHAR(200),
    is_primary              BOOLEAN NOT NULL DEFAULT FALSE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE
);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS sales_reps (
    sales_rep_id            BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    rep_code                VARCHAR(50) NOT NULL,
    rep_name_ar             VARCHAR(200) NOT NULL,
    rep_name_en             VARCHAR(200),
    user_id                 UUID REFERENCES users(user_id),
    default_warehouse_id    BIGINT REFERENCES warehouses(warehouse_id),
    commission_percentage   NUMERIC(5,2) DEFAULT 0,
    target_amount           NUMERIC(18,4) DEFAULT 0,
    phone                   VARCHAR(50),
    mobile                  VARCHAR(50),
    email                   VARCHAR(200),
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_sales_reps_code UNIQUE (company_id, rep_code)
);

CREATE TABLE IF NOT EXISTS sales_rep_targets (
    target_id               BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    sales_rep_id            BIGINT NOT NULL REFERENCES sales_reps(sales_rep_id) ON DELETE CASCADE,
    period_year             INT NOT NULL,
    period_month            INT,
    target_amount           NUMERIC(18,4) NOT NULL DEFAULT 0,
    achieved_amount         NUMERIC(18,4) DEFAULT 0,
    commission_amount       NUMERIC(18,4) DEFAULT 0,
    notes                   TEXT,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE
);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS departments (
    department_id           BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    department_code         VARCHAR(50) NOT NULL,
    department_name_ar      VARCHAR(200) NOT NULL,
    department_name_en      VARCHAR(200),
    manager_employee_id     BIGINT,
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    CONSTRAINT uq_departments_code UNIQUE (company_id, department_code)
);

CREATE TABLE IF NOT EXISTS job_titles (
    job_title_id            BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    job_code                VARCHAR(50) NOT NULL,
    job_name_ar             VARCHAR(200) NOT NULL,
    job_name_en             VARCHAR(200),
    department_id           BIGINT REFERENCES departments(department_id),
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    CONSTRAINT uq_job_titles_code UNIQUE (company_id, job_code)
);

CREATE TABLE IF NOT EXISTS employees (
    employee_id             BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    employee_code           VARCHAR(50) NOT NULL,
    employee_name_ar        VARCHAR(200) NOT NULL,
    employee_name_en        VARCHAR(200),
    department_id           BIGINT REFERENCES departments(department_id),
    job_title_id            BIGINT REFERENCES job_titles(job_title_id),
    branch_id               UUID REFERENCES branches(branch_id),
    hire_date               DATE,
    termination_date        DATE,
    basic_salary            NUMERIC(18,4) DEFAULT 0,
    allowances              NUMERIC(18,4) DEFAULT 0,
    national_id             VARCHAR(50),
    tax_number              VARCHAR(50),
    phone                   VARCHAR(50),
    mobile                  VARCHAR(50),
    email                   VARCHAR(200),
    address                 VARCHAR(500),
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_employees_code UNIQUE (company_id, employee_code)
);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS banks (
    bank_id                 BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    bank_code               VARCHAR(50) NOT NULL,
    bank_name_ar            VARCHAR(200) NOT NULL,
    bank_name_en            VARCHAR(200),
    swift_code              VARCHAR(20),
    phone                   VARCHAR(50),
    email                   VARCHAR(200),
    website                 VARCHAR(200),
    logo_path               VARCHAR(500),
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_banks_code UNIQUE (company_id, bank_code)
);

CREATE TABLE IF NOT EXISTS bank_accounts (
    account_id              BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    bank_id                 BIGINT NOT NULL REFERENCES banks(bank_id),
    account_number          VARCHAR(50) NOT NULL,
    account_name_ar         VARCHAR(200) NOT NULL,
    iban                    VARCHAR(50),
    currency_code           VARCHAR(3) DEFAULT 'SAR',
    gl_account_id           BIGINT REFERENCES chart_of_accounts(account_id),
    current_balance         NUMERIC(18,4) DEFAULT 0,
    opening_balance         NUMERIC(18,4) DEFAULT 0,
    branch_id               UUID REFERENCES branches(branch_id),
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    CONSTRAINT uq_bank_accounts UNIQUE (company_id, bank_id, account_number)
);

CREATE TABLE IF NOT EXISTS bank_transactions (
    transaction_id          BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    account_id              BIGINT NOT NULL REFERENCES bank_accounts(account_id),
    transaction_date        DATE NOT NULL DEFAULT CURRENT_DATE,
    transaction_type        VARCHAR(20) NOT NULL
        CHECK (transaction_type IN ('DEPOSIT','WITHDRAWAL','TRANSFER_IN','TRANSFER_OUT','FEE','INTEREST')),
    amount                  NUMERIC(18,4) NOT NULL DEFAULT 0,
    balance_after           NUMERIC(18,4) DEFAULT 0,
    reference_number        VARCHAR(50),
    description             VARCHAR(500),
    reconciled              BOOLEAN NOT NULL DEFAULT FALSE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    created_by              UUID,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS bom_headers (
    bom_id                  BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    bom_code                VARCHAR(50) NOT NULL,
    product_id              BIGINT NOT NULL REFERENCES products(product_id),
    version                 VARCHAR(20) DEFAULT '1.0',
    effective_date          DATE,
    expiry_date             DATE,
    quantity_produced       NUMERIC(18,4) DEFAULT 1,
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    notes                   TEXT,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_bom_headers UNIQUE (company_id, bom_code)
);

CREATE TABLE IF NOT EXISTS bom_lines (
    line_id                 BIGSERIAL PRIMARY KEY,
    bom_id                  BIGINT NOT NULL REFERENCES bom_headers(bom_id) ON DELETE CASCADE,
    component_product_id    BIGINT NOT NULL REFERENCES products(product_id),
    quantity                NUMERIC(18,6) NOT NULL DEFAULT 0,
    uom_id                  BIGINT REFERENCES units_of_measure(uom_id),
    waste_percentage        NUMERIC(5,2) DEFAULT 0,
    notes                   VARCHAR(500),
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE
);

CREATE TABLE IF NOT EXISTS production_orders (
    order_id                BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    order_number            VARCHAR(50) NOT NULL,
    order_date              DATE NOT NULL DEFAULT CURRENT_DATE,
    bom_id                  BIGINT REFERENCES bom_headers(bom_id),
    product_id              BIGINT NOT NULL REFERENCES products(product_id),
    planned_quantity        NUMERIC(18,4) NOT NULL DEFAULT 0,
    produced_quantity       NUMERIC(18,4) DEFAULT 0,
    warehouse_id            BIGINT REFERENCES warehouses(warehouse_id),
    start_date              DATE,
    end_date                DATE,
    status                  VARCHAR(20) NOT NULL DEFAULT 'DRAFT'
        CHECK (status IN ('DRAFT','APPROVED','IN_PROGRESS','COMPLETED','CANCELLED')),
    total_cost              NUMERIC(18,4) DEFAULT 0,
    notes                   TEXT,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE,
    created_by              UUID,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_production_orders UNIQUE (company_id, order_number)
);

CREATE TABLE IF NOT EXISTS production_order_lines (
    line_id                 BIGSERIAL PRIMARY KEY,
    order_id                BIGINT NOT NULL REFERENCES production_orders(order_id) ON DELETE CASCADE,
    product_id              BIGINT NOT NULL REFERENCES products(product_id),
    planned_quantity        NUMERIC(18,4) NOT NULL DEFAULT 0,
    consumed_quantity       NUMERIC(18,4) DEFAULT 0,
    unit_cost               NUMERIC(18,6) DEFAULT 0,
    total_cost              NUMERIC(18,6) DEFAULT 0,
    is_deleted              BOOLEAN NOT NULL DEFAULT FALSE
);

-- ========================================================================
-- ========================================================================

CREATE TABLE IF NOT EXISTS number_sequences (
    sequence_id             BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    sequence_key            VARCHAR(100) NOT NULL,
    prefix                  VARCHAR(20),
    suffix                  VARCHAR(20),
    next_number             BIGINT NOT NULL DEFAULT 1,
    padding                 INT NOT NULL DEFAULT 4,
    reset_yearly            BOOLEAN NOT NULL DEFAULT FALSE,
    last_reset_year         INT,
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_number_sequences UNIQUE (company_id, sequence_key)
);

CREATE TABLE IF NOT EXISTS system_settings (
    setting_id              BIGSERIAL PRIMARY KEY,
    company_id              UUID,
    setting_key             VARCHAR(100) NOT NULL,
    setting_value           TEXT,
    setting_type            VARCHAR(20) DEFAULT 'STRING',
    setting_group           VARCHAR(50),
    description             VARCHAR(500),
    is_encrypted            BOOLEAN NOT NULL DEFAULT FALSE,
    updated_by              UUID,
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_system_settings UNIQUE (company_id, setting_key)
);

CREATE TABLE IF NOT EXISTS audit_log (
    audit_id                BIGSERIAL PRIMARY KEY,
    company_id              UUID,
    user_id                 UUID,
    username                VARCHAR(100),
    action_type             VARCHAR(50) NOT NULL,
    entity_name             VARCHAR(100) NOT NULL,
    entity_id               VARCHAR(50),
    old_value               TEXT,
    new_value               TEXT,
    ip_address              VARCHAR(50),
    device_name             VARCHAR(200),
    notes                   TEXT,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_audit_entity ON audit_log(entity_name, entity_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_audit_user   ON audit_log(user_id, created_at DESC);

CREATE TABLE IF NOT EXISTS notifications (
    notification_id         BIGSERIAL PRIMARY KEY,
    company_id              UUID NOT NULL,
    user_id                 UUID,
    title                   VARCHAR(200) NOT NULL,
    message                 TEXT,
    notification_type       VARCHAR(20) DEFAULT 'INFO',
    reference_table         VARCHAR(50),
    reference_id            BIGINT,
    is_read                 BOOLEAN NOT NULL DEFAULT FALSE,
    read_at                 TIMESTAMPTZ,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_notif_user ON notifications(user_id, is_read, created_at DESC);

-- ========================================================================
-- [21] Seed Data
-- ========================================================================

INSERT INTO account_types (type_code, type_name_ar, type_name_en, normal_balance, sort_order)
VALUES
    ('ASSET',     'Assets',       'Assets',      'DEBIT',  1),
    ('LIABILITY', 'Liabilities',   'Liabilities', 'CREDIT', 2),
    ('EQUITY',    'Equity', 'Equity',      'CREDIT', 3),
    ('REVENUE',   'Revenue',    'Revenue',     'CREDIT', 4),
    ('EXPENSE',   'Expenses',    'Expenses',    'DEBIT',  5)
ON CONFLICT (type_code) DO NOTHING;

INSERT INTO currencies (currency_code, currency_name_ar, currency_name_en, symbol, decimal_places)
VALUES
    ('SAR', 'Saudi Riyal',   'Saudi Riyal',   'SAR', 2),
    ('USD', 'US Dollar', 'US Dollar',     '$',   2),
    ('EUR', 'Euro',         'Euro',          '€',   2),
    ('SYP', 'Syrian Pound',   'Syrian Pound',  'SYP', 2),
    ('TRY', 'Turkish Lira',   'Turkish Lira',  '₺',   2),
    ('AED', 'UAE Dirham', 'UAE Dirham',    'AED', 2)
ON CONFLICT (currency_code) DO NOTHING;

COMMIT;
