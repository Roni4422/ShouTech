-- PostgreSQL counterpart of sqlite/034_manufacturing.sql (Manufacturing, RS-8).
-- Money/quantities use NUMERIC on PostgreSQL; ids are uuid per 026 style.

CREATE TABLE IF NOT EXISTS mfg_orders (
    id uuid PRIMARY KEY,
    company_id TEXT NOT NULL,
    doc_no INTEGER NOT NULL,
    doc_date TEXT NOT NULL,
    item_code TEXT NOT NULL,
    item_name TEXT,
    qty NUMERIC NOT NULL CHECK (qty > 0),
    unit_code TEXT NOT NULL DEFAULT 'PCS',
    warehouse_code TEXT,
    overhead_amount NUMERIC NOT NULL DEFAULT 0 CHECK (overhead_amount >= 0),
    standard_unit_cost NUMERIC NOT NULL DEFAULT 0 CHECK (standard_unit_cost >= 0),
    total_cost NUMERIC NOT NULL DEFAULT 0,
    unit_cost NUMERIC NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Draft'
        CHECK (status IN ('Draft','Posted','Cancelled')),
    notes TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_mfg_orders_company_docno
    ON mfg_orders(company_id, doc_no) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS mfg_order_lines (
    id uuid PRIMARY KEY,
    order_id uuid NOT NULL REFERENCES mfg_orders(id),
    line_kind TEXT NOT NULL DEFAULT 'RAW',
    item_code TEXT NOT NULL,
    item_name TEXT,
    qty NUMERIC NOT NULL CHECK (qty > 0),
    unit_code TEXT,
    unit_cost NUMERIC NOT NULL DEFAULT 0 CHECK (unit_cost >= 0),
    standard_unit_cost NUMERIC NOT NULL DEFAULT 0 CHECK (standard_unit_cost >= 0),
    amount NUMERIC NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS ix_mfg_order_lines_order
    ON mfg_order_lines(order_id);
