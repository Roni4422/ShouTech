-- @shoutech-migration 014_core_runtime_schema.sql A4BE0B7B333937844F96825EC6F0BAEC8DBC74640750D5253817A2941BF76999 D1383791B8AE8C59521F56E9454E1657066C53AECAFE506C70A995CBE85C2110 D1383791B8AE8C59521F56E9454E1657066C53AECAFE506C70A995CBE85C2110 D1383791B8AE8C59521F56E9454E1657066C53AECAFE506C70A995CBE85C2110
-- These two lookup tables were historically created by Data.cs and were absent
-- from the ordered SQLite migration chain. They are now DBA-owned.
CREATE TABLE IF NOT EXISTS product_categories (
    category_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    parent_category_id TEXT,
    category_code TEXT NOT NULL,
    category_name_ar TEXT NOT NULL,
    category_name_en TEXT,
    category_level INTEGER NOT NULL DEFAULT 1,
    icon_path TEXT,
    image_path TEXT,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    deleted_at TEXT,
    deleted_by TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_by TEXT,
    modified_at TEXT,
    modified_by TEXT,
    UNIQUE(company_id, category_code),
    FOREIGN KEY(company_id) REFERENCES companies(company_id),
    FOREIGN KEY(parent_category_id) REFERENCES product_categories(category_id)
);

CREATE TABLE IF NOT EXISTS brands (
    brand_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    brand_code TEXT NOT NULL,
    brand_name_ar TEXT NOT NULL,
    brand_name_en TEXT,
    logo_path TEXT,
    website TEXT,
    description TEXT,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    deleted_at TEXT,
    deleted_by TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_by TEXT,
    modified_at TEXT,
    modified_by TEXT,
    UNIQUE(company_id, brand_code),
    FOREIGN KEY(company_id) REFERENCES companies(company_id)
);
-- @shoutech-migration 018_material_group_items.sql 6D161B281AC0E662375C3AC9170EB74F3730462A36D392B3C38C0489801FC97A A7DD9CCBCB7FB48D5BA4186EBBDCF4D0ACB7ED0E4EA341E7EB0DE4704F07ED8E A7DD9CCBCB7FB48D5BA4186EBBDCF4D0ACB7ED0E4EA341E7EB0DE4704F07ED8E A7DD9CCBCB7FB48D5BA4186EBBDCF4D0ACB7ED0E4EA341E7EB0DE4704F07ED8E
-- Material-to-group membership is many-to-many. Group headers remain in material_groups.
CREATE TABLE IF NOT EXISTS material_group_items (
    company_id TEXT NOT NULL,
    group_id INTEGER NOT NULL,
    material_id INTEGER NOT NULL,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_by TEXT,
    PRIMARY KEY (company_id, group_id, material_id),
    FOREIGN KEY (group_id, company_id)
        REFERENCES material_groups(group_id, company_id)
        ON DELETE CASCADE,
    FOREIGN KEY (material_id, company_id)
        REFERENCES materials(material_id, company_id)
        ON DELETE CASCADE
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_material_groups_id_company
    ON material_groups(group_id, company_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_materials_id_company
    ON materials(material_id, company_id);
CREATE INDEX IF NOT EXISTS ix_material_group_items_material
    ON material_group_items(company_id, material_id);

INSERT OR IGNORE INTO material_group_items
    (company_id, group_id, material_id, created_at, created_by)
SELECT m.company_id, g.group_id, m.material_id, CURRENT_TIMESTAMP, m.created_by
FROM materials m
INNER JOIN material_groups g
    ON g.company_id = m.company_id
   AND g.group_code = m.group_code
WHERE COALESCE(m.group_code, '') <> ''
  AND COALESCE(m.is_deleted, 0) = 0
  AND COALESCE(g.is_deleted, 0) = 0;
-- @shoutech-migration 031_master_cards.sql 1A76052B2D408EE83EE44BA4F87A44DF6F34E1067B4AFB956CEE504CD484ACD8 80AD178CB142636E1AEF0DD7CDFAE9CEAA6E284BF3828F3A82C923732B07D7AC 80AD178CB142636E1AEF0DD7CDFAE9CEAA6E284BF3828F3A82C923732B07D7AC 80AD178CB142636E1AEF0DD7CDFAE9CEAA6E284BF3828F3A82C923732B07D7AC
-- Owns the Master Cards module (ribbon completion chunk B): cost centers and
-- their allocations, banks, currencies and dated exchange rates, parties
-- (customers/suppliers) and their groups, sales reps and their groups.
-- Transaction lines keep referencing cost centers and accounts by code (the
-- codebase-wide convention); codes are unique per company among live rows.
-- Runtime services must not create tables.

CREATE TABLE IF NOT EXISTS cost_centers (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT,
    parent_id TEXT REFERENCES cost_centers(id),
    is_group INTEGER NOT NULL DEFAULT 0,
    level INTEGER NOT NULL DEFAULT 0,
    is_active INTEGER NOT NULL DEFAULT 1,
    notes TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_cost_centers_company_code
    ON cost_centers(company_id, code) WHERE is_deleted = 0;

CREATE INDEX IF NOT EXISTS ix_cost_centers_parent
    ON cost_centers(company_id, parent_id);

CREATE TABLE IF NOT EXISTS cost_center_allocations (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    main_cost_center_id TEXT NOT NULL REFERENCES cost_centers(id),
    valid_from TEXT NOT NULL,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_cost_center_allocations_main
    ON cost_center_allocations(company_id, main_cost_center_id);

CREATE TABLE IF NOT EXISTS cost_center_allocation_lines (
    id TEXT PRIMARY KEY,
    allocation_id TEXT NOT NULL REFERENCES cost_center_allocations(id),
    cost_center_id TEXT NOT NULL REFERENCES cost_centers(id),
    percentage REAL NOT NULL CHECK (percentage > 0 AND percentage <= 100)
);

CREATE INDEX IF NOT EXISTS ix_cost_center_allocation_lines_allocation
    ON cost_center_allocation_lines(allocation_id);

CREATE TABLE IF NOT EXISTS banks (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT,
    branch_name TEXT,
    account_no TEXT,
    iban TEXT,
    currency_code TEXT NOT NULL DEFAULT 'SAR',
    gl_account_code TEXT,
    is_active INTEGER NOT NULL DEFAULT 1,
    notes TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_banks_company_code
    ON banks(company_id, code) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS currencies (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT,
    symbol TEXT,
    fraction_name TEXT,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_currencies_company_code
    ON currencies(company_id, code) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS currency_rates (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    currency_code TEXT NOT NULL,
    rate_date TEXT NOT NULL,
    rate REAL NOT NULL CHECK (rate > 0),
    min_rate REAL,
    max_rate REAL,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_currency_rates_company_currency_date
    ON currency_rates(company_id, currency_code, rate_date);

CREATE TABLE IF NOT EXISTS party_groups (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('C','S')),
    code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT,
    parent_id TEXT REFERENCES party_groups(id),
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_party_groups_company_kind_code
    ON party_groups(company_id, kind, code) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS parties (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('C','S')),
    code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT,
    party_group_id TEXT REFERENCES party_groups(id),
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
    is_active INTEGER NOT NULL DEFAULT 1,
    notes TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_parties_company_kind_code
    ON parties(company_id, kind, code) WHERE is_deleted = 0;

CREATE INDEX IF NOT EXISTS ix_parties_group
    ON parties(company_id, party_group_id);

CREATE TABLE IF NOT EXISTS sales_reps (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT,
    phone TEXT,
    commission_pct REAL NOT NULL DEFAULT 0,
    target_amount REAL NOT NULL DEFAULT 0,
    group_id TEXT REFERENCES sales_rep_groups(id),
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_sales_reps_company_code
    ON sales_reps(company_id, code) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS sales_rep_groups (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_sales_rep_groups_company_code
    ON sales_rep_groups(company_id, code) WHERE is_deleted = 0;
-- @shoutech-migration 050_card_manufacturing_persistence.sql B1BD92ACD90420A3C84DFBAB0383AD8E0C4A43FC6D43AB697AB5548451F7B424 E2763868A84F988504AFDEE0BA14A471BD00AA9E929A268869E2D816BD49127B E2763868A84F988504AFDEE0BA14A471BD00AA9E929A268869E2D816BD49127B E2763868A84F988504AFDEE0BA14A471BD00AA9E929A268869E2D816BD49127B
-- Legacy windows rebuild (persistence completion): creates the master-data
-- tables the card windows (CstCard, BnkCard, CurCard, RepCard) and the
-- manufacturing order window (MfgWin) write to. customers/suppliers were
-- already referenced by TaxSvc.ResolveParty, RptQuery balances/aging and
-- DataExportSvc/RecycleBinSvc catalogues but were never created by any
-- migration; this migration closes that schema gap. Runtime services must
-- not create tables; all DDL lives here and in the PostgreSQL counterpart.

CREATE TABLE IF NOT EXISTS customers (
    customer_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
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
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT,
    deleted_at TEXT,
    deleted_by TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_customers_company_code
    ON customers(company_id, customer_code) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_customers_company_name
    ON customers(company_id, customer_name_ar);

CREATE TABLE IF NOT EXISTS suppliers (
    supplier_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
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
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT,
    deleted_at TEXT,
    deleted_by TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_suppliers_company_code
    ON suppliers(company_id, supplier_code) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_suppliers_company_name
    ON suppliers(company_id, supplier_name_ar);

CREATE TABLE IF NOT EXISTS bank_accounts (
    bank_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    bank_code TEXT NOT NULL,
    bank_name TEXT NOT NULL,
    branch TEXT NOT NULL DEFAULT '',
    account_no TEXT NOT NULL DEFAULT '',
    iban TEXT NOT NULL DEFAULT '',
    currency_code TEXT NOT NULL DEFAULT 'SAR',
    gl_account_code TEXT NOT NULL DEFAULT '',
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT,
    deleted_at TEXT,
    deleted_by TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_bank_accounts_company_code
    ON bank_accounts(company_id, bank_code) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS currencies (
    currency_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    currency_code TEXT NOT NULL,
    currency_name_ar TEXT NOT NULL DEFAULT '',
    currency_name_en TEXT NOT NULL DEFAULT '',
    symbol TEXT NOT NULL DEFAULT '',
    fraction_name TEXT NOT NULL DEFAULT '',
    exchange_rate NUMERIC NOT NULL DEFAULT 1,
    min_rate NUMERIC NOT NULL DEFAULT 0,
    max_rate NUMERIC NOT NULL DEFAULT 0,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT,
    deleted_at TEXT,
    deleted_by TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_currencies_company_code
    ON currencies(company_id, currency_code) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS currency_rates (
    rate_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    currency_code TEXT NOT NULL,
    rate_date TEXT NOT NULL,
    rate NUMERIC NOT NULL DEFAULT 0,
    buy_rate NUMERIC NOT NULL DEFAULT 0,
    sell_rate NUMERIC NOT NULL DEFAULT 0,
    notes TEXT NOT NULL DEFAULT '',
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_currency_rates_lookup
    ON currency_rates(company_id, currency_code, rate_date);

CREATE TABLE IF NOT EXISTS sales_reps (
    rep_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    rep_code TEXT NOT NULL,
    rep_name TEXT NOT NULL,
    phone TEXT NOT NULL DEFAULT '',
    commission_percent NUMERIC NOT NULL DEFAULT 0,
    monthly_target NUMERIC NOT NULL DEFAULT 0,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT,
    deleted_at TEXT,
    deleted_by TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_sales_reps_company_code
    ON sales_reps(company_id, rep_code) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS sales_rep_targets (
    target_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    rep_id INTEGER NOT NULL,
    period TEXT NOT NULL,
    target_amount NUMERIC NOT NULL DEFAULT 0,
    actual_sales NUMERIC NOT NULL DEFAULT 0,
    commission_earned NUMERIC NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT '',
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT,
    UNIQUE(company_id, rep_id, period)
);
CREATE INDEX IF NOT EXISTS ix_sales_rep_targets_rep
    ON sales_rep_targets(company_id, rep_id, period);

CREATE TABLE IF NOT EXISTS manufacturing_orders (
    order_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    order_no TEXT NOT NULL,
    order_date TEXT NOT NULL,
    finished_material_id INTEGER,
    finished_material_code TEXT NOT NULL DEFAULT '',
    finished_material_name TEXT NOT NULL DEFAULT '',
    quantity NUMERIC NOT NULL DEFAULT 0 CHECK (quantity >= 0),
    unit_code TEXT NOT NULL DEFAULT '',
    warehouse_id INTEGER,
    warehouse_code TEXT NOT NULL DEFAULT '',
    overhead_cost NUMERIC NOT NULL DEFAULT 0,
    total_cost NUMERIC NOT NULL DEFAULT 0,
    unit_cost NUMERIC NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Draft' CHECK (status IN ('Draft','Posted','Cancelled')),
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT,
    deleted_at TEXT,
    deleted_by TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_manufacturing_orders_company_no
    ON manufacturing_orders(company_id, order_no) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS manufacturing_order_lines (
    line_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    order_id INTEGER NOT NULL,
    line_no INTEGER NOT NULL,
    material_id INTEGER,
    material_code TEXT NOT NULL DEFAULT '',
    material_name TEXT NOT NULL DEFAULT '',
    unit_code TEXT NOT NULL DEFAULT '',
    quantity NUMERIC NOT NULL DEFAULT 0,
    unit_cost NUMERIC NOT NULL DEFAULT 0,
    total_cost NUMERIC NOT NULL DEFAULT 0,
    FOREIGN KEY(order_id) REFERENCES manufacturing_orders(order_id)
);
CREATE INDEX IF NOT EXISTS ix_manufacturing_order_lines_order
    ON manufacturing_order_lines(company_id, order_id, line_no);
-- @shoutech-migration 070_material_language_neutral_name.sql EECC3F40FBD41C6C43AACB26AE25B01F5ABAAD73BA2B462ACCF8A6B098F03561 9BF6E4C0B5307FEE9FC713B68E26E74BEEF5FDD77F6C82BECEE775D53664F579 9BF6E4C0B5307FEE9FC713B68E26E74BEEF5FDD77F6C82BECEE775D53664F579 9BF6E4C0B5307FEE9FC713B68E26E74BEEF5FDD77F6C82BECEE775D53664F579
-- Material names are stored in one language-neutral column (materials.name).
-- Fresh databases receive this definition from the inventory base schema.
-- Legacy development databases are intentionally discarded; no compatibility migration is required.
-- @shoutech-migration 095_party_structure.sql 8E67182E9707C7E7F5EE31A49BC5C7C153A9868517C7F363164AFB549B185FFB 885FBDDD3C2D0D6B323149F69A621E294E8C9C69F9537BE20730C45CFC84BA38 885FBDDD3C2D0D6B323149F69A621E294E8C9C69F9537BE20730C45CFC84BA38 885FBDDD3C2D0D6B323149F69A621E294E8C9C69F9537BE20730C45CFC84BA38
-- ============================================================================
-- 095 - Customer management completion (SQLite)
--
-- Sales cycle Phase 01 (W1, docs/SALES_CYCLE_UPGRADE.md Part II):
--   * party_contacts / party_addresses / party_branches: the multi-row
--     party structure §1 requires (the card's single phone/email/address
--     fields stay as the PRIMARY contact/address summary and are kept in
--     sync by MasterCardRepository).
--   * parties header additions: default_currency, risk_class, blacklist
--     (flag + reason + at/by), and the opening-balance marker
--     (amount + date + journal ref; one opening entry per party).
--   * sales_documents.due_date: doc_date + party credit_days for SAL
--     invoices; PartyAging uses it as its primary aging basis.
--   * credit_overrides: the audit row PartyCreditGuard writes when a
--     SALES.CREDIT.OVERRIDE holder authorizes posting past the limit.
--   * SALES.CREDIT.OVERRIDE permission seed (083 pattern; administrators
--     only by default).
--
-- Idempotent: CREATE TABLE IF NOT EXISTS, column adds guarded by the
-- migrator directives, permission seed INSERT OR IGNORE + UPDATE.
-- ============================================================================

CREATE TABLE IF NOT EXISTS party_contacts (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    party_id TEXT NOT NULL REFERENCES parties(id),
    name TEXT NOT NULL,
    role TEXT,
    phone TEXT,
    mobile TEXT,
    email TEXT,
    is_primary INTEGER NOT NULL DEFAULT 0,
    is_active INTEGER NOT NULL DEFAULT 1,
    notes TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_party_contacts_party
    ON party_contacts(company_id, party_id) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS party_addresses (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    party_id TEXT NOT NULL REFERENCES parties(id),
    kind TEXT NOT NULL DEFAULT 'Delivery' CHECK (kind IN ('Delivery','Billing','Other')),
    city TEXT,
    country TEXT,
    postal_code TEXT,
    address TEXT,
    is_default INTEGER NOT NULL DEFAULT 0,
    is_active INTEGER NOT NULL DEFAULT 1,
    notes TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_party_addresses_party
    ON party_addresses(company_id, party_id) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS party_branches (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    party_id TEXT NOT NULL REFERENCES parties(id),
    name TEXT NOT NULL,
    address TEXT,
    phone TEXT,
    is_active INTEGER NOT NULL DEFAULT 1,
    notes TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_party_branches_party
    ON party_branches(company_id, party_id) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS credit_overrides (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    party_kind TEXT NOT NULL,
    party_id INTEGER NOT NULL,
    doc_kind TEXT,
    doc_no TEXT,
    current_balance REAL NOT NULL DEFAULT 0,
    document_amount REAL NOT NULL DEFAULT 0,
    credit_limit REAL NOT NULL DEFAULT 0,
    actor TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS ix_credit_overrides_party
    ON credit_overrides(company_id, party_kind, party_id);

-- ---- Party header additions -------------------------------------------------
-- @if-column-missing parties.default_currency
ALTER TABLE parties ADD COLUMN default_currency TEXT;
-- @if-column-missing parties.risk_class
ALTER TABLE parties ADD COLUMN risk_class TEXT;
-- @if-column-missing parties.is_blacklisted
ALTER TABLE parties ADD COLUMN is_blacklisted INTEGER NOT NULL DEFAULT 0;
-- @if-column-missing parties.blacklist_reason
ALTER TABLE parties ADD COLUMN blacklist_reason TEXT;
-- @if-column-missing parties.blacklisted_at
ALTER TABLE parties ADD COLUMN blacklisted_at TEXT;
-- @if-column-missing parties.blacklisted_by
ALTER TABLE parties ADD COLUMN blacklisted_by TEXT;
-- @if-column-missing parties.opening_balance_amount
ALTER TABLE parties ADD COLUMN opening_balance_amount REAL;
-- @if-column-missing parties.opening_balance_date
ALTER TABLE parties ADD COLUMN opening_balance_date TEXT;
-- @if-column-missing parties.opening_journal_id
ALTER TABLE parties ADD COLUMN opening_journal_id TEXT;

-- ---- Sales document due dates ------------------------------------------------
-- @if-column-missing sales_documents.due_date
ALTER TABLE sales_documents ADD COLUMN due_date TEXT;

-- ---- SALES.CREDIT.OVERRIDE permission (083 pattern) ---------------------------
INSERT OR IGNORE INTO permissions
    (permission_code, permission_name, group_code, feature_code, operation_code,
     name_ar, name_en, description, display_order, is_sensitive)
VALUES
    ('SALES.CREDIT.OVERRIDE', 'Sales - Override Credit Limit',
     'SALES', 'CREDIT', 'OVERRIDE',
     'المبيعات - تجاوز حد الائتمان',
     'Sales - Override Credit Limit',
     'Post a sales document past the party credit limit; every override is audited', 344, 1);

UPDATE permissions
SET group_code='SALES', feature_code='CREDIT', operation_code='OVERRIDE',
    name_ar='المبيعات - تجاوز حد الائتمان',
    name_en='Sales - Override Credit Limit',
    description='Post a sales document past the party credit limit; every override is audited',
    display_order=344, is_sensitive=1, is_active=1
WHERE permission_code='SALES.CREDIT.OVERRIDE';

INSERT OR IGNORE INTO role_permissions(role_id, permission_id)
SELECT r.role_id, p.permission_id
FROM roles r CROSS JOIN permissions p
WHERE r.role_code='ROLE_ADMINISTRATOR' AND r.is_active=1 AND p.is_active=1
  AND p.permission_code='SALES.CREDIT.OVERRIDE';
-- @shoutech-migration 117_label_designs.sql E43998CC0B5300AD4B17E59B8EA2386C127AD7AC9C1A26FD0FFC7FFCF93680AA 4C68B51F4FF31F08BD208DC554DDB8B0515277020FD1AD27EFE71ECF3D605D96 4C68B51F4FF31F08BD208DC554DDB8B0515277020FD1AD27EFE71ECF3D605D96 4C68B51F4FF31F08BD208DC554DDB8B0515277020FD1AD27EFE71ECF3D605D96
-- 117 - Barcode Designer store (services phase B4).
-- label_designs holds named barcode-label designs per company
-- (JSON payload, LabelDesign): label size, symbology (Code 128 /
-- Code 39), module/bar sizes, fonts, colors, visible elements.
-- IsDefault marks the design the label print paths use. The
-- designer's local working file (barcode-design.cfg) remains as
-- the fallback when no DB design exists.

CREATE TABLE IF NOT EXISTS label_designs (
    DesignID INTEGER PRIMARY KEY AUTOINCREMENT,
    CompanyID TEXT NOT NULL,
    DesignName TEXT NOT NULL,
    IsDefault INTEGER NOT NULL DEFAULT 0,
    PayloadJson TEXT NOT NULL,
    IsDeleted INTEGER NOT NULL DEFAULT 0,
    CreatedBy TEXT,
    UpdatedBy TEXT,
    CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt TEXT,
    DeletedAt TEXT,
    DeletedBy TEXT
);

CREATE INDEX IF NOT EXISTS ix_label_designs_company
    ON label_designs(CompanyID, IsDeleted);
-- @shoutech-migration 121_uom_names.sql 066A42679221382580CA7F07C21A1E26718EC056E2448A70A6DFBE36890DE518 FCAD883B8896CDEC4793EFA2F9BF1232131312F699B93FAC488BEE1533FAF981 FCAD883B8896CDEC4793EFA2F9BF1232131312F699B93FAC488BEE1533FAF981 FCAD883B8896CDEC4793EFA2F9BF1232131312F699B93FAC488BEE1533FAF981
-- 121 - Unit-of-measure names (R4 study outcome). units_of_measure
-- carried only uom_code since migration 001; every benchmark names
-- its units, and the definition editor should too. Additive:
-- all existing consumers key on uom_code and are untouched.

-- @if-column-missing units_of_measure.name_en
ALTER TABLE units_of_measure ADD COLUMN name_en TEXT;
-- @if-column-missing units_of_measure.name_ar
ALTER TABLE units_of_measure ADD COLUMN name_ar TEXT;
-- @shoutech-migration 126_barcode_template.sql 1D1A1BFC42E3DBFC67DA192AD0BF9F67427F3ACD37D336A6D39CF2614A35F6CD 52324C01ED0F416F3990D11CE1828D3192EF8FA73809647116A8107B6D08FFEB 52324C01ED0F416F3990D11CE1828D3192EF8FA73809647116A8107B6D08FFEB 52324C01ED0F416F3990D11CE1828D3192EF8FA73809647116A8107B6D08FFEB
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
-- @shoutech-migration 127_material_name_is_language_neutral.sql EECC3F40FBD41C6C43AACB26AE25B01F5ABAAD73BA2B462ACCF8A6B098F03561 9BF6E4C0B5307FEE9FC713B68E26E74BEEF5FDD77F6C82BECEE775D53664F579 9BF6E4C0B5307FEE9FC713B68E26E74BEEF5FDD77F6C82BECEE775D53664F579 9BF6E4C0B5307FEE9FC713B68E26E74BEEF5FDD77F6C82BECEE775D53664F579
-- Material names are stored in one language-neutral column (materials.name).
-- Fresh databases receive this definition from the inventory base schema.
-- Legacy development databases are intentionally discarded; no compatibility migration is required.
-- @shoutech-migration 129_product_category_language_neutral_name.sql 38F4BD654A2D3D193E02BCDA5949821574793D1BB0764A186EF012BADEF8858F 3CC70968CFDEB6F2BCF93A4550C0200225A53A96B0269AFCE4D74B6F96D79613 3CC70968CFDEB6F2BCF93A4550C0200225A53A96B0269AFCE4D74B6F96D79613 3CC70968CFDEB6F2BCF93A4550C0200225A53A96B0269AFCE4D74B6F96D79613
-- Product category names are user-language data, not separate Arabic/English fields.
-- Preserve the existing Arabic value when present; otherwise retain the English value.
ALTER TABLE product_categories RENAME COLUMN category_name_ar TO name;
UPDATE product_categories
SET name = category_name_en
WHERE TRIM(COALESCE(name, '')) = ''
  AND TRIM(COALESCE(category_name_en, '')) <> '';
ALTER TABLE product_categories DROP COLUMN category_name_en;
