PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS companies (
    company_id TEXT NOT NULL PRIMARY KEY,
    company_code TEXT NOT NULL UNIQUE,
    company_name_ar TEXT NOT NULL,
    company_name_en TEXT NOT NULL,
    email TEXT,
    phone TEXT,
    mobile TEXT,
    address_line1 TEXT,
    commercial_registration TEXT,
    currency_code TEXT NOT NULL DEFAULT 'USD',
    language_code TEXT NOT NULL DEFAULT 'ar',
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS users (
    user_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    username TEXT NOT NULL,
    password_hash TEXT NOT NULL,
    password_salt TEXT NOT NULL,
    full_name TEXT NOT NULL,
    full_name_ar TEXT NOT NULL,
    full_name_en TEXT NOT NULL,
    email TEXT,
    must_change_password INTEGER NOT NULL DEFAULT 0,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(company_id, username),
    FOREIGN KEY(company_id) REFERENCES companies(company_id)
);

CREATE TABLE IF NOT EXISTS fiscal_years (
    fiscal_year_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    fiscal_year INTEGER NOT NULL,
    database_name TEXT NOT NULL,
    start_date TEXT NOT NULL,
    end_date TEXT NOT NULL,
    is_current INTEGER NOT NULL DEFAULT 1,
    is_closed INTEGER NOT NULL DEFAULT 0,
    UNIQUE(company_id, fiscal_year),
    FOREIGN KEY(company_id) REFERENCES companies(company_id)
);

CREATE TABLE IF NOT EXISTS roles (
    role_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    role_code TEXT NOT NULL,
    role_name TEXT NOT NULL,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    UNIQUE(company_id, role_code),
    FOREIGN KEY(company_id) REFERENCES companies(company_id)
);

CREATE TABLE IF NOT EXISTS permissions (
    permission_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    permission_code TEXT NOT NULL UNIQUE,
    permission_name TEXT NOT NULL,
    is_active INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS user_roles (
    user_id TEXT NOT NULL,
    role_id INTEGER NOT NULL,
    PRIMARY KEY (user_id, role_id),
    FOREIGN KEY(user_id) REFERENCES users(user_id),
    FOREIGN KEY(role_id) REFERENCES roles(role_id)
);

CREATE TABLE IF NOT EXISTS role_permissions (
    role_id INTEGER NOT NULL,
    permission_id INTEGER NOT NULL,
    PRIMARY KEY (role_id, permission_id),
    FOREIGN KEY(role_id) REFERENCES roles(role_id),
    FOREIGN KEY(permission_id) REFERENCES permissions(permission_id)
);

CREATE TABLE IF NOT EXISTS units_of_measure (
    uom_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    uom_code TEXT NOT NULL,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    UNIQUE(company_id, uom_code),
    FOREIGN KEY(company_id) REFERENCES companies(company_id)
);

CREATE TABLE IF NOT EXISTS products (
    product_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    product_code TEXT NOT NULL,
    product_name_ar TEXT NOT NULL,
    product_name_en TEXT,
    category_id INTEGER NOT NULL,
    primary_uom_id INTEGER NOT NULL,
    default_sale_price NUMERIC NOT NULL DEFAULT 0,
    default_purchase_price NUMERIC NOT NULL DEFAULT 0,
    cost_price NUMERIC NOT NULL DEFAULT 0,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_by TEXT,
    updated_at TEXT,
    deleted_by TEXT,
    deleted_at TEXT,
    UNIQUE(company_id, product_code),
    FOREIGN KEY(company_id) REFERENCES companies(company_id),
    FOREIGN KEY(primary_uom_id) REFERENCES units_of_measure(uom_id)
);

CREATE TABLE IF NOT EXISTS journal_entries (
    journal_entry_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    entry_date TEXT NOT NULL,
    description TEXT NOT NULL,
    status INTEGER NOT NULL DEFAULT 0 CHECK (status IN (0, 1, 2)),
    created_by TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    posted_by TEXT,
    posted_at TEXT,
    reversed_by TEXT,
    reversed_at TEXT
);

CREATE TABLE IF NOT EXISTS journal_entry_lines (
    journal_entry_line_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    journal_entry_id TEXT NOT NULL,
    line_number INTEGER NOT NULL,
    account_id TEXT NOT NULL,
    debit NUMERIC NOT NULL DEFAULT 0,
    credit NUMERIC NOT NULL DEFAULT 0,
    cost_center TEXT,
    note TEXT,
    UNIQUE(journal_entry_id, line_number),
    CHECK (debit >= 0 AND credit >= 0 AND ((debit > 0 AND credit = 0) OR (credit > 0 AND debit = 0))),
    FOREIGN KEY(journal_entry_id) REFERENCES journal_entries(journal_entry_id)
);

CREATE TABLE IF NOT EXISTS journal_audit_events (
    journal_audit_event_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    journal_entry_id TEXT NOT NULL,
    event_type INTEGER NOT NULL CHECK (event_type IN (1, 2, 3)),
    actor TEXT NOT NULL,
    event_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    details TEXT,
    FOREIGN KEY(journal_entry_id) REFERENCES journal_entries(journal_entry_id)
);

CREATE TABLE IF NOT EXISTS stock_balances (
    stock_balance_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    warehouse_id INTEGER NOT NULL,
    product_id INTEGER NOT NULL,
    available_qty NUMERIC(18,6) NOT NULL DEFAULT 0,
    last_cost NUMERIC,
    last_updated TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    UNIQUE (company_id, warehouse_id, product_id)
);

CREATE TABLE IF NOT EXISTS stock_movements (
    movement_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    movement_type TEXT NOT NULL,
    movement_date TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    warehouse_id_to INTEGER,
    product_id INTEGER NOT NULL,
    quantity NUMERIC(18,6) NOT NULL CHECK (quantity > 0),
    unit_cost NUMERIC,
    reference_table TEXT,
    notes TEXT,
    created_by TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
