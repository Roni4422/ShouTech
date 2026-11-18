-- ============================================================================
-- 066 - POS full build (SQLite)
--
-- Build POS (see docs/BUILD_POS.md). PosWin / PosCheckoutWin already
-- existed as a barcode cart posting through InvRepo (real GL step 3,
-- party settlement step 4, stored totals step 5, engine valuation
-- steps 6/11, batch/serial capture 054) -- but there was no till
-- around it: no session, no split tender, no hold, no POS return, no
-- receipt, and the day-close (037 pos_cash_closings) counted the wrong
-- documents. This migration creates the session world:
--   pos_sessions        one row per cashier shift on a terminal:
--                       open (opening cash count) -> sales -> close
--                       (counted cash, expected-vs-counted variance)
--   pos_sale_tenders    one row per tender applied to a POS sale or
--                       POS return (Cash / Card / OnAccount); refund
--                       rows carry NEGATIVE amounts so a session's
--                       expected cash is a plain SUM over Cash rows.
--   pos_held_bills(+lines)  parked bills, persisted (not UI state)
--   pos_settings        per-company POS settings; the session-less
--                       quick mode lives here as an EXPLICIT flag
--                       (allow_sessionless, default 0 = sessions are
--                       mandatory) plus terminal/warehouse defaults.
-- sales_documents gains pos_session_id: every POS document (sale or
-- return) posted inside a session carries its session id, so the Z
-- report and the drawer expectation read session data, never a
-- company-wide day guess (the 037 CloseCashWin defect).
--
-- Company is keyed by CODE (TEXT) -- the party/POS world, like 065.
-- Idempotent: the checksum ledger skips a re-run; inside the file
-- every CREATE is IF NOT EXISTS, the ALTER follows the 062/064 plain
-- ADD COLUMN precedent (guarded by the ledger), and permission seeds
-- are INSERT OR IGNORE + UPDATE.
-- ============================================================================

-- ---- Sessions ---------------------------------------------------------------
CREATE TABLE IF NOT EXISTS pos_sessions (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    session_no TEXT NOT NULL,
    terminal_code TEXT NOT NULL,
    cashier TEXT NOT NULL DEFAULT '',
    warehouse_id INTEGER,
    currency_code TEXT NOT NULL DEFAULT 'SAR',
    opening_cash REAL NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Open'
        CHECK (status IN ('Open','Closed')),
    opened_at TEXT NOT NULL DEFAULT (datetime('now')),
    opened_by TEXT,
    closed_at TEXT,
    closed_by TEXT,
    counted_cash REAL,
    expected_cash REAL,
    variance REAL,
    notes TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_pos_sessions_company_no
    ON pos_sessions(company_id, session_no) WHERE is_deleted = 0;
-- At most ONE open session per terminal per company.
CREATE UNIQUE INDEX IF NOT EXISTS ux_pos_sessions_open_terminal
    ON pos_sessions(company_id, terminal_code)
    WHERE status = 'Open' AND is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_pos_sessions_company_status
    ON pos_sessions(company_id, status) WHERE is_deleted = 0;

-- ---- Tenders ------------------------------------------------------------------
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
    amount REAL NOT NULL DEFAULT 0,
    tendered_amount REAL,
    change_amount REAL NOT NULL DEFAULT 0,
    card_ref TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_pos_tenders_session
    ON pos_sale_tenders(company_id, session_id) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_pos_tenders_doc
    ON pos_sale_tenders(company_id, doc_id, doc_kind) WHERE is_deleted = 0;

-- ---- Held (parked) bills --------------------------------------------------------
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
    bill_discount REAL NOT NULL DEFAULT 0,
    notes TEXT,
    status TEXT NOT NULL DEFAULT 'Held'
        CHECK (status IN ('Held','Recalled','Cancelled')),
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_pos_held_company_no
    ON pos_held_bills(company_id, held_no) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_pos_held_company_status
    ON pos_held_bills(company_id, status) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS pos_held_bill_lines (
    id TEXT PRIMARY KEY,
    held_id TEXT NOT NULL REFERENCES pos_held_bills(id),
    line_no INTEGER NOT NULL,
    material_id INTEGER,
    material_code TEXT,
    description TEXT NOT NULL DEFAULT '',
    unit TEXT,
    qty REAL NOT NULL DEFAULT 0,
    unit_price REAL NOT NULL DEFAULT 0,
    discount_pct REAL NOT NULL DEFAULT 0,
    tax_rate REAL NOT NULL DEFAULT 0,
    warehouse_id INTEGER
);
CREATE INDEX IF NOT EXISTS ix_pos_held_lines_held
    ON pos_held_bill_lines(held_id, line_no);

-- ---- Settings -------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS pos_settings (
    company_id TEXT PRIMARY KEY,
    allow_sessionless INTEGER NOT NULL DEFAULT 0,
    default_terminal_code TEXT,
    default_warehouse_id INTEGER,
    updated_by TEXT,
    updated_at TEXT
);

-- ---- Document -> session link -----------------------------------------------------
ALTER TABLE sales_documents ADD COLUMN pos_session_id TEXT;
CREATE INDEX IF NOT EXISTS ix_sales_documents_pos_session
    ON sales_documents(company_id, pos_session_id) WHERE is_deleted = 0;

-- ---- Permissions (Build POS 066; PermCat.Pos additions) ---------------------------
-- POS.SESSION.OPEN / CLOSE / REOPEN, POS.INVOICE.REFUND, POS.PRICE.OVERRIDE
-- and POS.DISCOUNT.MANAGE already exist (004). New here: the discount
-- OVERRIDE (applying a discount at the till -- sensitive), holding /
-- recalling bills, and the session-less bypass (sensitive: selling
-- outside a session is an audited exception, never an accident).
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('POS.DISCOUNT.OVERRIDE','POS Discount - Override','POS','DISCOUNT','OVERRIDE','POS Discount - Override','POS Discount - Override','Apply line or bill discounts at the till (Build POS 066)',383,1);
UPDATE permissions SET group_code='POS',feature_code='DISCOUNT',operation_code='OVERRIDE',name_ar='POS Discount - Override',name_en='POS Discount - Override',display_order=383,is_sensitive=1 WHERE permission_code='POS.DISCOUNT.OVERRIDE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('POS.HOLD.MANAGE','POS Hold - Manage','POS','HOLD','MANAGE','POS Hold - Manage','POS Hold - Manage','Park (hold) and recall POS bills (Build POS 066)',384,0);
UPDATE permissions SET group_code='POS',feature_code='HOLD',operation_code='MANAGE',name_ar='POS Hold - Manage',name_en='POS Hold - Manage',display_order=384,is_sensitive=0 WHERE permission_code='POS.HOLD.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('POS.SESSION.BYPASS','POS Session - Bypass','POS','SESSION','BYPASS','POS Session - Bypass','POS Session - Bypass','Sell without an open session when the company enables session-less mode (Build POS 066)',385,1);
UPDATE permissions SET group_code='POS',feature_code='SESSION',operation_code='BYPASS',name_ar='POS Session - Bypass',name_en='POS Session - Bypass',display_order=385,is_sensitive=1 WHERE permission_code='POS.SESSION.BYPASS';

INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('POS.DISCOUNT.OVERRIDE','POS.HOLD.MANAGE','POS.SESSION.BYPASS');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('POS.HOLD.MANAGE');
