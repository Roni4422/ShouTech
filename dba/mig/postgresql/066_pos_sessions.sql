-- ============================================================================
-- 066 - POS full build (PostgreSQL)
--
-- PostgreSQL counterpart of sqlite/066_pos_sessions.sql (Build POS,
-- see docs/BUILD_POS.md). Differences: NUMERIC money/quantities,
-- BOOLEAN flags, ADD COLUMN IF NOT EXISTS, ON CONFLICT permission
-- seeds. Tables: pos_sessions (cashier shift lifecycle),
-- pos_sale_tenders (split tenders; refunds negative), pos_held_bills
-- (+ lines), pos_settings (explicit session-less flag, default off);
-- sales_documents gains pos_session_id.
-- ============================================================================

CREATE TABLE IF NOT EXISTS pos_sessions (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    session_no TEXT NOT NULL,
    terminal_code TEXT NOT NULL,
    cashier TEXT NOT NULL DEFAULT '',
    warehouse_id INTEGER,
    currency_code TEXT NOT NULL DEFAULT 'SAR',
    opening_cash NUMERIC NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Open'
        CHECK (status IN ('Open','Closed')),
    opened_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    opened_by TEXT,
    closed_at TIMESTAMPTZ,
    closed_by TEXT,
    counted_cash NUMERIC,
    expected_cash NUMERIC,
    variance NUMERIC,
    notes TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_pos_sessions_company_no
    ON pos_sessions(company_id, session_no) WHERE is_deleted = FALSE;
CREATE UNIQUE INDEX IF NOT EXISTS ux_pos_sessions_open_terminal
    ON pos_sessions(company_id, terminal_code)
    WHERE status = 'Open' AND is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_pos_sessions_company_status
    ON pos_sessions(company_id, status) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS pos_sale_tenders (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    session_id TEXT REFERENCES pos_sessions(id),
    doc_id INTEGER NOT NULL,
    doc_no TEXT NOT NULL DEFAULT '',
    doc_kind TEXT NOT NULL DEFAULT 'POS'
        CHECK (doc_kind IN ('POS','SRT')),
    tender_type TEXT NOT NULL
        CHECK (tender_type IN ('Cash','Card','OnAccount')),
    amount NUMERIC NOT NULL DEFAULT 0,
    tendered_amount NUMERIC,
    change_amount NUMERIC NOT NULL DEFAULT 0,
    card_ref TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_pos_tenders_session
    ON pos_sale_tenders(company_id, session_id) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_pos_tenders_doc
    ON pos_sale_tenders(company_id, doc_id, doc_kind) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS pos_held_bills (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    session_id TEXT REFERENCES pos_sessions(id),
    held_no TEXT NOT NULL,
    cashier TEXT NOT NULL DEFAULT '',
    party_id INTEGER,
    party_code TEXT,
    party_name TEXT,
    currency_code TEXT NOT NULL DEFAULT 'SAR',
    bill_discount NUMERIC NOT NULL DEFAULT 0,
    notes TEXT,
    status TEXT NOT NULL DEFAULT 'Held'
        CHECK (status IN ('Held','Recalled','Cancelled')),
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_pos_held_company_no
    ON pos_held_bills(company_id, held_no) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_pos_held_company_status
    ON pos_held_bills(company_id, status) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS pos_held_bill_lines (
    id TEXT PRIMARY KEY,
    held_id TEXT NOT NULL REFERENCES pos_held_bills(id),
    line_no INTEGER NOT NULL,
    material_id INTEGER,
    material_code TEXT,
    description TEXT NOT NULL DEFAULT '',
    unit TEXT,
    qty NUMERIC NOT NULL DEFAULT 0,
    unit_price NUMERIC NOT NULL DEFAULT 0,
    discount_pct NUMERIC NOT NULL DEFAULT 0,
    tax_rate NUMERIC NOT NULL DEFAULT 0,
    warehouse_id INTEGER
);
CREATE INDEX IF NOT EXISTS ix_pos_held_lines_held
    ON pos_held_bill_lines(held_id, line_no);

CREATE TABLE IF NOT EXISTS pos_settings (
    company_id TEXT PRIMARY KEY,
    allow_sessionless BOOLEAN NOT NULL DEFAULT FALSE,
    default_terminal_code TEXT,
    default_warehouse_id INTEGER,
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);

ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS pos_session_id TEXT;
-- sales_documents.is_deleted is retained as a legacy integer flag in the
-- canonical commercial-document schema; cast through text for compatibility.
CREATE INDEX IF NOT EXISTS ix_sales_documents_pos_session
    ON sales_documents(company_id, pos_session_id)
    WHERE is_deleted::text IN ('0', 'false');

INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('POS.DISCOUNT.OVERRIDE','POS Discount - Override','POS','DISCOUNT','OVERRIDE','POS Discount - Override','POS Discount - Override','Apply line or bill discounts at the till (Build POS 066)',383,true) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('POS.HOLD.MANAGE','POS Hold - Manage','POS','HOLD','MANAGE','POS Hold - Manage','POS Hold - Manage','Park (hold) and recall POS bills (Build POS 066)',384,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('POS.SESSION.BYPASS','POS Session - Bypass','POS','SESSION','BYPASS','POS Session - Bypass','POS Session - Bypass','Sell without an open session when the company enables session-less mode (Build POS 066)',385,true) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('POS.DISCOUNT.OVERRIDE','POS.HOLD.MANAGE','POS.SESSION.BYPASS') ON CONFLICT DO NOTHING;
INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('POS.HOLD.MANAGE') ON CONFLICT DO NOTHING;
