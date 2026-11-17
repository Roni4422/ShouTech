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
