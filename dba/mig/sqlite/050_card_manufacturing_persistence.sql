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
