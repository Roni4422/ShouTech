-- @shoutech-migration 017_catalogs.sql B25D4A763BC42E2A19B1C476EBDC784CFD6F94CAE123E1A982FA5D55C6491AE8 B25D4A763BC42E2A19B1C476EBDC784CFD6F94CAE123E1A982FA5D55C6491AE8
-- PostgreSQL counterpart of sqlite/014_core_runtime_schema.sql.
-- These lookup tables are DBA-owned and must not be bootstrapped by Data.cs.
CREATE TABLE IF NOT EXISTS product_categories (
    category_id TEXT PRIMARY KEY,
    company_id UUID NOT NULL,
    parent_category_id TEXT,
    category_code TEXT NOT NULL,
    category_name_ar TEXT NOT NULL,
    category_name_en TEXT,
    category_level INTEGER NOT NULL DEFAULT 1,
    icon_path TEXT,
    image_path TEXT,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    deleted_at TIMESTAMPTZ,
    deleted_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_by TEXT,
    modified_at TIMESTAMPTZ,
    modified_by TEXT,
    UNIQUE(company_id, category_code),
    FOREIGN KEY(company_id) REFERENCES companies(company_id),
    FOREIGN KEY(parent_category_id) REFERENCES product_categories(category_id)
);

CREATE TABLE IF NOT EXISTS brands (
    brand_id TEXT PRIMARY KEY,
    company_id UUID NOT NULL,
    brand_code TEXT NOT NULL,
    brand_name_ar TEXT NOT NULL,
    brand_name_en TEXT,
    logo_path TEXT,
    website TEXT,
    description TEXT,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    deleted_at TIMESTAMPTZ,
    deleted_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_by TEXT,
    modified_at TIMESTAMPTZ,
    modified_by TEXT,
    UNIQUE(company_id, brand_code),
    FOREIGN KEY(company_id) REFERENCES companies(company_id)
);
-- @shoutech-migration 018_material_group_items.sql 675873BB2D3710DD9B77751C335DFD14469543DE639C8449E0C75BB1C330A3D8 675873BB2D3710DD9B77751C335DFD14469543DE639C8449E0C75BB1C330A3D8
-- Material-to-group membership is many-to-many. Group headers remain in material_groups.
CREATE UNIQUE INDEX IF NOT EXISTS ux_material_groups_id_company
    ON material_groups(group_id, company_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_materials_id_company
    ON materials(material_id, company_id);
CREATE TABLE IF NOT EXISTS material_group_items (
    company_id TEXT NOT NULL,
    group_id BIGINT NOT NULL,
    material_id BIGINT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_by TEXT,
    PRIMARY KEY (company_id, group_id, material_id),
    FOREIGN KEY (group_id, company_id)
        REFERENCES material_groups(group_id, company_id)
        ON DELETE CASCADE,
    FOREIGN KEY (material_id, company_id)
        REFERENCES materials(material_id, company_id)
        ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_material_group_items_material
    ON material_group_items(company_id, material_id);

INSERT INTO material_group_items
    (company_id, group_id, material_id, created_at, created_by)
SELECT m.company_id, g.group_id, m.material_id, CURRENT_TIMESTAMP, m.created_by
FROM materials m
INNER JOIN material_groups g
    ON g.company_id = m.company_id
   AND g.group_code = m.group_code
WHERE COALESCE(m.group_code, '') <> ''
  AND COALESCE(m.is_deleted, FALSE) = FALSE
  AND COALESCE(g.is_deleted, 0) = 0
ON CONFLICT (company_id, group_id, material_id) DO NOTHING;
-- @shoutech-migration 031_master_cards.sql 353A993C09A5A3DE84B5D7901B5A69CC938E32F30454D37EF05B3CB99997EAD9 353A993C09A5A3DE84B5D7901B5A69CC938E32F30454D37EF05B3CB99997EAD9
-- PostgreSQL counterpart of sqlite/031_master_cards.sql (Master Cards module:
-- cost centers + allocations, banks, currencies + rates, parties + groups,
-- sales reps + groups). Runtime services must not create tables.

CREATE TABLE IF NOT EXISTS cost_centers (
    id uuid PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT,
    parent_id uuid REFERENCES cost_centers(id),
    is_group BOOLEAN NOT NULL DEFAULT FALSE,
    level INTEGER NOT NULL DEFAULT 0,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    notes TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_cost_centers_company_code
    ON cost_centers(company_id, code) WHERE is_deleted = FALSE;

CREATE INDEX IF NOT EXISTS ix_cost_centers_parent
    ON cost_centers(company_id, parent_id);

CREATE TABLE IF NOT EXISTS cost_center_allocations (
    id uuid PRIMARY KEY,
    company_id TEXT NOT NULL,
    main_cost_center_id uuid NOT NULL REFERENCES cost_centers(id),
    valid_from TEXT NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS ix_cost_center_allocations_main
    ON cost_center_allocations(company_id, main_cost_center_id);

CREATE TABLE IF NOT EXISTS cost_center_allocation_lines (
    id uuid PRIMARY KEY,
    allocation_id uuid NOT NULL REFERENCES cost_center_allocations(id),
    cost_center_id uuid NOT NULL REFERENCES cost_centers(id),
    percentage REAL NOT NULL CHECK (percentage > 0 AND percentage <= 100)
);

CREATE INDEX IF NOT EXISTS ix_cost_center_allocation_lines_allocation
    ON cost_center_allocation_lines(allocation_id);

CREATE TABLE IF NOT EXISTS banks (
    id uuid PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT,
    branch_name TEXT,
    account_no TEXT,
    iban TEXT,
    currency_code TEXT NOT NULL DEFAULT 'SAR',
    gl_account_code TEXT,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    notes TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_banks_company_code
    ON banks(company_id, code) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS currencies (
    id uuid PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT,
    symbol TEXT,
    fraction_name TEXT,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_currencies_company_code
    ON currencies(company_id, code) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS currency_rates (
    id uuid PRIMARY KEY,
    company_id TEXT NOT NULL,
    currency_code TEXT NOT NULL,
    rate_date TEXT NOT NULL,
    rate REAL NOT NULL CHECK (rate > 0),
    min_rate REAL,
    max_rate REAL,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS ix_currency_rates_company_currency_date
    ON currency_rates(company_id, currency_code, rate_date);

CREATE TABLE IF NOT EXISTS party_groups (
    id uuid PRIMARY KEY,
    company_id TEXT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('C','S')),
    code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT,
    parent_id uuid REFERENCES party_groups(id),
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_party_groups_company_kind_code
    ON party_groups(company_id, kind, code) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS parties (
    id uuid PRIMARY KEY,
    company_id TEXT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('C','S')),
    code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT,
    party_group_id uuid REFERENCES party_groups(id),
    tax_no TEXT,
    commercial_reg_no TEXT,
    credit_limit REAL NOT NULL DEFAULT 0,
    credit_days INTEGER NOT NULL DEFAULT 30,
    price_tier TEXT,
    gl_account_code TEXT,
    phone TEXT,
    mobile TEXT,
    email TEXT,
    address TEXT,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    notes TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_parties_company_kind_code
    ON parties(company_id, kind, code) WHERE is_deleted = FALSE;

CREATE INDEX IF NOT EXISTS ix_parties_group
    ON parties(company_id, party_group_id);

CREATE TABLE IF NOT EXISTS sales_rep_groups (
    id uuid PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_sales_rep_groups_company_code
    ON sales_rep_groups(company_id, code) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS sales_reps (
    id uuid PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT,
    phone TEXT,
    commission_pct REAL NOT NULL DEFAULT 0,
    target_amount REAL NOT NULL DEFAULT 0,
    group_id uuid REFERENCES sales_rep_groups(id),
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_sales_reps_company_code
    ON sales_reps(company_id, code) WHERE is_deleted = FALSE;
-- @shoutech-migration 050_card_manufacturing_persistence.sql 175F57661DAF5E17409AE9F4A011A38366F008D83F0B1204CCE5E50305AB106E 175F57661DAF5E17409AE9F4A011A38366F008D83F0B1204CCE5E50305AB106E
-- PostgreSQL counterpart of sqlite/050_card_manufacturing_persistence.sql.
-- Creates the master-data tables the card windows (CstCard, BnkCard,
-- CurCard, RepCard) and the manufacturing order window (MfgWin) write to.
-- customers/suppliers were already referenced by TaxSvc.ResolveParty,
-- RptQuery balances/aging and the export/recycle-bin catalogues but were
-- never created by any migration; this migration closes that schema gap.
-- is_deleted/is_active stay INT so the shared application SQL
-- (is_deleted = 0) works unchanged on both providers, matching the
-- commercial-document tables in 012_commercial_documents.sql.

CREATE TABLE IF NOT EXISTS customers (
    customer_id BIGSERIAL PRIMARY KEY,
    company_id TEXT NOT NULL,
    customer_code TEXT NOT NULL,
    customer_name_ar TEXT NOT NULL,
    customer_name_en TEXT NOT NULL DEFAULT '',
    party_group TEXT NOT NULL DEFAULT '',
    tax_number TEXT NOT NULL DEFAULT '',
    commercial_reg TEXT NOT NULL DEFAULT '',
    credit_limit NUMERIC NOT NULL DEFAULT 0,
    credit_days INTEGER NOT NULL DEFAULT 0,
    price_tier TEXT NOT NULL DEFAULT '',
    gl_account_code TEXT NOT NULL DEFAULT '',
    phone TEXT NOT NULL DEFAULT '',
    mobile TEXT NOT NULL DEFAULT '',
    email TEXT NOT NULL DEFAULT '',
    address TEXT NOT NULL DEFAULT '',
    is_active INT NOT NULL DEFAULT 1,
    is_deleted INT NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ,
    deleted_at TIMESTAMPTZ,
    deleted_by TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_customers_company_code
    ON customers(company_id, customer_code) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_customers_company_name
    ON customers(company_id, customer_name_ar);

CREATE TABLE IF NOT EXISTS suppliers (
    supplier_id BIGSERIAL PRIMARY KEY,
    company_id TEXT NOT NULL,
    supplier_code TEXT NOT NULL,
    supplier_name_ar TEXT NOT NULL,
    supplier_name_en TEXT NOT NULL DEFAULT '',
    party_group TEXT NOT NULL DEFAULT '',
    tax_number TEXT NOT NULL DEFAULT '',
    commercial_reg TEXT NOT NULL DEFAULT '',
    credit_limit NUMERIC NOT NULL DEFAULT 0,
    credit_days INTEGER NOT NULL DEFAULT 0,
    price_tier TEXT NOT NULL DEFAULT '',
    gl_account_code TEXT NOT NULL DEFAULT '',
    phone TEXT NOT NULL DEFAULT '',
    mobile TEXT NOT NULL DEFAULT '',
    email TEXT NOT NULL DEFAULT '',
    address TEXT NOT NULL DEFAULT '',
    is_active INT NOT NULL DEFAULT 1,
    is_deleted INT NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ,
    deleted_at TIMESTAMPTZ,
    deleted_by TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_suppliers_company_code
    ON suppliers(company_id, supplier_code) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_suppliers_company_name
    ON suppliers(company_id, supplier_name_ar);

CREATE TABLE IF NOT EXISTS bank_accounts (
    bank_id BIGSERIAL PRIMARY KEY,
    company_id TEXT NOT NULL,
    bank_code TEXT NOT NULL,
    bank_name TEXT NOT NULL,
    branch TEXT NOT NULL DEFAULT '',
    account_no TEXT NOT NULL DEFAULT '',
    iban TEXT NOT NULL DEFAULT '',
    currency_code TEXT NOT NULL DEFAULT 'SAR',
    gl_account_code TEXT NOT NULL DEFAULT '',
    is_active INT NOT NULL DEFAULT 1,
    is_deleted INT NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ,
    deleted_at TIMESTAMPTZ,
    deleted_by TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_bank_accounts_company_code
    ON bank_accounts(company_id, bank_code) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS currencies (
    currency_id BIGSERIAL PRIMARY KEY,
    company_id TEXT NOT NULL,
    currency_code TEXT NOT NULL,
    currency_name_ar TEXT NOT NULL DEFAULT '',
    currency_name_en TEXT NOT NULL DEFAULT '',
    symbol TEXT NOT NULL DEFAULT '',
    fraction_name TEXT NOT NULL DEFAULT '',
    exchange_rate NUMERIC NOT NULL DEFAULT 1,
    min_rate NUMERIC NOT NULL DEFAULT 0,
    max_rate NUMERIC NOT NULL DEFAULT 0,
    is_active INT NOT NULL DEFAULT 1,
    is_deleted INT NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ,
    deleted_at TIMESTAMPTZ,
    deleted_by TEXT
);
-- Migration 031 owns the canonical currencies table (code/is_deleted).
-- Migration 050 must not index the legacy column names from its retired
-- duplicate table definition.
CREATE UNIQUE INDEX IF NOT EXISTS ux_currencies_company_code
    ON currencies(company_id, code)
    WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS currency_rates (
    rate_id BIGSERIAL PRIMARY KEY,
    company_id TEXT NOT NULL,
    currency_code TEXT NOT NULL,
    rate_date TEXT NOT NULL,
    rate NUMERIC NOT NULL DEFAULT 0,
    buy_rate NUMERIC NOT NULL DEFAULT 0,
    sell_rate NUMERIC NOT NULL DEFAULT 0,
    notes TEXT NOT NULL DEFAULT '',
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_currency_rates_lookup
    ON currency_rates(company_id, currency_code, rate_date);

CREATE TABLE IF NOT EXISTS sales_reps (
    rep_id BIGSERIAL PRIMARY KEY,
    company_id TEXT NOT NULL,
    rep_code TEXT NOT NULL,
    rep_name TEXT NOT NULL,
    phone TEXT NOT NULL DEFAULT '',
    commission_percent NUMERIC NOT NULL DEFAULT 0,
    monthly_target NUMERIC NOT NULL DEFAULT 0,
    is_active INT NOT NULL DEFAULT 1,
    is_deleted INT NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ,
    deleted_at TIMESTAMPTZ,
    deleted_by TEXT
);
-- Migration 031 owns the canonical sales_reps table (code/is_deleted BOOLEAN).
CREATE UNIQUE INDEX IF NOT EXISTS ux_sales_reps_company_code
    ON sales_reps(company_id, code) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS sales_rep_targets (
    target_id BIGSERIAL PRIMARY KEY,
    company_id TEXT NOT NULL,
    rep_id BIGINT NOT NULL,
    period TEXT NOT NULL,
    target_amount NUMERIC NOT NULL DEFAULT 0,
    actual_sales NUMERIC NOT NULL DEFAULT 0,
    commission_earned NUMERIC NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT '',
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ,
    UNIQUE(company_id, rep_id, period)
);
CREATE INDEX IF NOT EXISTS ix_sales_rep_targets_rep
    ON sales_rep_targets(company_id, rep_id, period);

CREATE TABLE IF NOT EXISTS manufacturing_orders (
    order_id BIGSERIAL PRIMARY KEY,
    company_id TEXT NOT NULL,
    order_no TEXT NOT NULL,
    order_date TEXT NOT NULL,
    finished_material_id BIGINT,
    finished_material_code TEXT NOT NULL DEFAULT '',
    finished_material_name TEXT NOT NULL DEFAULT '',
    quantity NUMERIC NOT NULL DEFAULT 0 CHECK (quantity >= 0),
    unit_code TEXT NOT NULL DEFAULT '',
    warehouse_id BIGINT,
    warehouse_code TEXT NOT NULL DEFAULT '',
    overhead_cost NUMERIC NOT NULL DEFAULT 0,
    total_cost NUMERIC NOT NULL DEFAULT 0,
    unit_cost NUMERIC NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Draft' CHECK (status IN ('Draft','Posted','Cancelled')),
    is_deleted INT NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ,
    deleted_at TIMESTAMPTZ,
    deleted_by TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_manufacturing_orders_company_no
    ON manufacturing_orders(company_id, order_no) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS manufacturing_order_lines (
    line_id BIGSERIAL PRIMARY KEY,
    company_id TEXT NOT NULL,
    order_id BIGINT NOT NULL REFERENCES manufacturing_orders(order_id),
    line_no INTEGER NOT NULL,
    material_id BIGINT,
    material_code TEXT NOT NULL DEFAULT '',
    material_name TEXT NOT NULL DEFAULT '',
    unit_code TEXT NOT NULL DEFAULT '',
    quantity NUMERIC NOT NULL DEFAULT 0,
    unit_cost NUMERIC NOT NULL DEFAULT 0,
    total_cost NUMERIC NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_manufacturing_order_lines_order
    ON manufacturing_order_lines(company_id, order_id, line_no);
-- @shoutech-migration 070_material_language_neutral_name.sql 9BF6E4C0B5307FEE9FC713B68E26E74BEEF5FDD77F6C82BECEE775D53664F579 9BF6E4C0B5307FEE9FC713B68E26E74BEEF5FDD77F6C82BECEE775D53664F579
-- Material names are stored in one language-neutral column (materials.name).
-- Fresh databases receive this definition from the inventory base schema.
-- Legacy development databases are intentionally discarded; no compatibility migration is required.
-- @shoutech-migration 095_party_structure.sql 4ADC04D3F372F23622050296B82A02D7020AE29D9EABA278450225B0462DAFE2 4ADC04D3F372F23622050296B82A02D7020AE29D9EABA278450225B0462DAFE2
-- ============================================================================
-- 095 - Customer management completion (PostgreSQL)
-- Mirror of sqlite/095_party_structure.sql: party contacts/addresses/
-- branches, party header additions (default currency, risk class,
-- blacklist, opening-balance marker), sales_documents.due_date, the
-- credit_overrides audit table, and the SALES.CREDIT.OVERRIDE seed.
-- Idempotent: CREATE TABLE IF NOT EXISTS, ADD COLUMN IF NOT EXISTS,
-- permission upsert ON CONFLICT.
-- ============================================================================

CREATE TABLE IF NOT EXISTS party_contacts (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    party_id UUID NOT NULL REFERENCES parties(id),
    name TEXT NOT NULL,
    role TEXT,
    phone TEXT,
    mobile TEXT,
    email TEXT,
    is_primary BOOLEAN NOT NULL DEFAULT false,
    is_active BOOLEAN NOT NULL DEFAULT true,
    notes TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT false,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (now()::text),
    updated_by TEXT,
    updated_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_party_contacts_party
    ON party_contacts(company_id, party_id) WHERE is_deleted = false;

CREATE TABLE IF NOT EXISTS party_addresses (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    party_id UUID NOT NULL REFERENCES parties(id),
    kind TEXT NOT NULL DEFAULT 'Delivery' CHECK (kind IN ('Delivery','Billing','Other')),
    city TEXT,
    country TEXT,
    postal_code TEXT,
    address TEXT,
    is_default BOOLEAN NOT NULL DEFAULT false,
    is_active BOOLEAN NOT NULL DEFAULT true,
    notes TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT false,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (now()::text),
    updated_by TEXT,
    updated_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_party_addresses_party
    ON party_addresses(company_id, party_id) WHERE is_deleted = false;

CREATE TABLE IF NOT EXISTS party_branches (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    party_id UUID NOT NULL REFERENCES parties(id),
    name TEXT NOT NULL,
    address TEXT,
    phone TEXT,
    is_active BOOLEAN NOT NULL DEFAULT true,
    notes TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT false,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (now()::text),
    updated_by TEXT,
    updated_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_party_branches_party
    ON party_branches(company_id, party_id) WHERE is_deleted = false;

CREATE TABLE IF NOT EXISTS credit_overrides (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    party_kind TEXT NOT NULL,
    party_id BIGINT NOT NULL,
    doc_kind TEXT,
    doc_no TEXT,
    current_balance NUMERIC NOT NULL DEFAULT 0,
    document_amount NUMERIC NOT NULL DEFAULT 0,
    credit_limit NUMERIC NOT NULL DEFAULT 0,
    actor TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (now()::text)
);

CREATE INDEX IF NOT EXISTS ix_credit_overrides_party
    ON credit_overrides(company_id, party_kind, party_id);

ALTER TABLE parties ADD COLUMN IF NOT EXISTS default_currency TEXT;
ALTER TABLE parties ADD COLUMN IF NOT EXISTS risk_class TEXT;
ALTER TABLE parties ADD COLUMN IF NOT EXISTS is_blacklisted BOOLEAN NOT NULL DEFAULT false;
ALTER TABLE parties ADD COLUMN IF NOT EXISTS blacklist_reason TEXT;
ALTER TABLE parties ADD COLUMN IF NOT EXISTS blacklisted_at TEXT;
ALTER TABLE parties ADD COLUMN IF NOT EXISTS blacklisted_by TEXT;
ALTER TABLE parties ADD COLUMN IF NOT EXISTS opening_balance_amount NUMERIC;
ALTER TABLE parties ADD COLUMN IF NOT EXISTS opening_balance_date TEXT;
ALTER TABLE parties ADD COLUMN IF NOT EXISTS opening_journal_id TEXT;

ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS due_date TEXT;

INSERT INTO permissions
    (permission_code, permission_name, group_code, feature_code, operation_code,
     name_ar, name_en, description, display_order, is_sensitive)
VALUES
    ('SALES.CREDIT.OVERRIDE', 'Sales - Override Credit Limit',
     'SALES', 'CREDIT', 'OVERRIDE',
     'المبيعات - تجاوز حد الائتمان',
     'Sales - Override Credit Limit',
     'Post a sales document past the party credit limit; every override is audited', 344, true)
ON CONFLICT(permission_code) DO UPDATE SET
    permission_name=EXCLUDED.permission_name,
    group_code=EXCLUDED.group_code,
    feature_code=EXCLUDED.feature_code,
    operation_code=EXCLUDED.operation_code,
    name_ar=EXCLUDED.name_ar,
    name_en=EXCLUDED.name_en,
    description=EXCLUDED.description,
    display_order=EXCLUDED.display_order,
    is_sensitive=EXCLUDED.is_sensitive,
    is_active=true;

INSERT INTO role_permissions(role_id, permission_id)
SELECT r.role_id, p.permission_id
FROM roles r CROSS JOIN permissions p
WHERE r.role_code='ROLE_ADMINISTRATOR' AND r.is_active=true AND p.is_active=true
  AND p.permission_code='SALES.CREDIT.OVERRIDE'
ON CONFLICT DO NOTHING;
-- @shoutech-migration 117_label_designs.sql 972EE05EB7B4F588A1F7A105FF158575E9D44635E2331582301305C512435992 972EE05EB7B4F588A1F7A105FF158575E9D44635E2331582301305C512435992
-- 117 - Barcode Designer store (services phase B4).
-- See the SQLite companion for the design note. Flag columns are
-- INTEGER (0/1): the app filters IsDeleted = 0 / IsDefault = 1 on
-- both providers.

CREATE TABLE IF NOT EXISTS label_designs (
    DesignID INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    CompanyID TEXT NOT NULL,
    DesignName TEXT NOT NULL,
    IsDefault INTEGER NOT NULL DEFAULT 0,
    PayloadJson TEXT NOT NULL,
    IsDeleted INTEGER NOT NULL DEFAULT 0,
    CreatedBy TEXT,
    UpdatedBy TEXT,
    CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
    UpdatedAt TIMESTAMP,
    DeletedAt TIMESTAMP,
    DeletedBy TEXT
);

CREATE INDEX IF NOT EXISTS ix_label_designs_company
    ON label_designs(CompanyID, IsDeleted);
-- @shoutech-migration 121_uom_names.sql FCAD883B8896CDEC4793EFA2F9BF1232131312F699B93FAC488BEE1533FAF981 FCAD883B8896CDEC4793EFA2F9BF1232131312F699B93FAC488BEE1533FAF981
-- 121 - Unit-of-measure names (R4 study outcome). units_of_measure
-- carried only uom_code since migration 001; every benchmark names
-- its units, and the definition editor should too. Additive:
-- all existing consumers key on uom_code and are untouched.

-- @if-column-missing units_of_measure.name_en
ALTER TABLE units_of_measure ADD COLUMN name_en TEXT;
-- @if-column-missing units_of_measure.name_ar
ALTER TABLE units_of_measure ADD COLUMN name_ar TEXT;
-- @shoutech-migration 126_barcode_template.sql 52324C01ED0F416F3990D11CE1828D3192EF8FA73809647116A8107B6D08FFEB 52324C01ED0F416F3990D11CE1828D3192EF8FA73809647116A8107B6D08FFEB
-- 126 - Composite barcode template (docs/OPTWIN_SECTIONS.md §3.4,
-- Landing 3). One row per company: the active scale-label template
-- (prefix + fixed segment positions) the scan paths split incoming
-- codes with. No row (or is_active = 0) = no composite parsing.
CREATE TABLE IF NOT EXISTS barcode_templates (
    company_id      TEXT    NOT NULL PRIMARY KEY,
    prefix          TEXT    NOT NULL,
    total_length    INTEGER NOT NULL,
    code_start      INTEGER NOT NULL,
    code_length     INTEGER NOT NULL,
    wint_start      INTEGER NOT NULL,
    wint_length     INTEGER NOT NULL,
    wfrac_start     INTEGER NOT NULL,
    wfrac_length    INTEGER NOT NULL,
    is_active       INTEGER NOT NULL DEFAULT 1,
    updated_by      TEXT    NULL,
    updated_at      TEXT    NULL
);
-- @shoutech-migration 127_material_name_is_language_neutral.sql 9BF6E4C0B5307FEE9FC713B68E26E74BEEF5FDD77F6C82BECEE775D53664F579 9BF6E4C0B5307FEE9FC713B68E26E74BEEF5FDD77F6C82BECEE775D53664F579
-- Material names are stored in one language-neutral column (materials.name).
-- Fresh databases receive this definition from the inventory base schema.
-- Legacy development databases are intentionally discarded; no compatibility migration is required.
-- @shoutech-migration 129_product_category_language_neutral_name.sql 08A138E8FA20175B14DC46A3E36494363F3C2605A5AB5B694E650329A2F31609 08A138E8FA20175B14DC46A3E36494363F3C2605A5AB5B694E650329A2F31609
-- Product category names are user-language data, not separate Arabic/English fields.
-- Preserve the existing Arabic value when present; otherwise retain the English value.
ALTER TABLE product_categories RENAME COLUMN category_name_ar TO name;
UPDATE product_categories
SET name = category_name_en
WHERE BTRIM(COALESCE(name, '')) = ''
  AND BTRIM(COALESCE(category_name_en, '')) <> '';
ALTER TABLE product_categories DROP COLUMN category_name_en;
