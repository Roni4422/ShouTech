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
    ('ASSET',     'أصول',       'Assets',      'DEBIT',  1),
    ('LIABILITY', 'التزامات',   'Liabilities', 'CREDIT', 2),
    ('EQUITY',    'حقوق ملكية', 'Equity',      'CREDIT', 3),
    ('REVENUE',   'إيرادات',    'Revenue',     'CREDIT', 4),
    ('EXPENSE',   'مصروفات',    'Expenses',    'DEBIT',  5)
ON CONFLICT (type_code) DO NOTHING;

INSERT INTO currencies (currency_code, currency_name_ar, currency_name_en, symbol, decimal_places)
VALUES
    ('SAR', 'ريال سعودي',   'Saudi Riyal',   'ر.س', 2),
    ('USD', 'دولار أمريكي', 'US Dollar',     '$',   2),
    ('EUR', 'يورو',         'Euro',          '€',   2),
    ('SYP', 'ليرة سورية',   'Syrian Pound',  'ل.س', 2),
    ('TRY', 'ليرة تركية',   'Turkish Lira',  '₺',   2),
    ('AED', 'درهم إماراتي', 'UAE Dirham',    'د.إ', 2)
ON CONFLICT (currency_code) DO NOTHING;

COMMIT;
