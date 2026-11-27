-- Owns Manufacturing orders (RS-8): mfg_orders + mfg_order_lines.
-- Posting consumes raw materials and produces the finished item through
-- material_stock_movements in one transaction (see MfgOrderRepository).
-- Dates are stored as ISO text (yyyy-MM-dd) on both providers.
-- Runtime services must not create tables.

CREATE TABLE IF NOT EXISTS mfg_orders (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    doc_no INTEGER NOT NULL,
    doc_date TEXT NOT NULL,
    item_code TEXT NOT NULL,
    item_name TEXT,
    qty REAL NOT NULL CHECK (qty > 0),
    unit_code TEXT NOT NULL DEFAULT 'PCS',
    warehouse_code TEXT,
    overhead_amount REAL NOT NULL DEFAULT 0 CHECK (overhead_amount >= 0),
    standard_unit_cost REAL NOT NULL DEFAULT 0 CHECK (standard_unit_cost >= 0),
    total_cost REAL NOT NULL DEFAULT 0,
    unit_cost REAL NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Draft'
        CHECK (status IN ('Draft','Posted','Cancelled')),
    notes TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_mfg_orders_company_docno
    ON mfg_orders(company_id, doc_no) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS mfg_order_lines (
    id TEXT PRIMARY KEY,
    order_id TEXT NOT NULL REFERENCES mfg_orders(id),
    line_kind TEXT NOT NULL DEFAULT 'RAW',
    item_code TEXT NOT NULL,
    item_name TEXT,
    qty REAL NOT NULL CHECK (qty > 0),
    unit_code TEXT,
    unit_cost REAL NOT NULL DEFAULT 0 CHECK (unit_cost >= 0),
    standard_unit_cost REAL NOT NULL DEFAULT 0 CHECK (standard_unit_cost >= 0),
    amount REAL NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS ix_mfg_order_lines_order
    ON mfg_order_lines(order_id);
