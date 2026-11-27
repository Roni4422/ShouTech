-- @shoutech-migration 037_operations.sql FA28C22CF653ED287C07B418C5B67B312869B81CAFF775CD608ACBFD448C7ED1 4DB182253386AC69E1670AC05EAF9DB1EC14EB7567F57B13E233C74EA9760046 4DB182253386AC69E1670AC05EAF9DB1EC14EB7567F57B13E233C74EA9760046 4DB182253386AC69E1670AC05EAF9DB1EC14EB7567F57B13E233C74EA9760046
-- Migration 037 (SQLite): operations tables (ribbon completion RS-12).
-- pos_cash_closings: one row per POS terminal cash closing (expected vs
-- counted, difference computed by the service, never typed by hand).
-- support_tickets: local ticket register for Support / Suggestion windows;
-- tickets are persisted locally (no mail server is assumed).
-- Runtime services must not create tables.

CREATE TABLE IF NOT EXISTS pos_cash_closings (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    terminal_code TEXT NOT NULL,
    business_date TEXT NOT NULL,
    opened_by TEXT,
    closed_by TEXT,
    opening_amount REAL NOT NULL DEFAULT 0,
    expected_amount REAL NOT NULL DEFAULT 0,
    counted_amount REAL NOT NULL DEFAULT 0,
    difference REAL NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Closed',
    notes TEXT,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_pos_cash_closings_company_date
    ON pos_cash_closings(company_id, business_date, terminal_code);

CREATE TABLE IF NOT EXISTS support_tickets (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    ticket_no INTEGER NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('SUPPORT', 'SUGGESTION')),
    subject TEXT NOT NULL,
    body TEXT NOT NULL,
    contact_email TEXT,
    status TEXT NOT NULL DEFAULT 'Open',
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT,
    UNIQUE (company_id, ticket_no)
);

CREATE INDEX IF NOT EXISTS ix_support_tickets_company_kind
    ON support_tickets(company_id, kind, status);
-- @shoutech-migration 066_pos_sessions.sql 254FA3C18850577EE659225C6A9D4081B7B38DB629A8AA772C148D07DF5091C4 B11AE06D3C4F151D33E84CFD6F0F4462153A5D69ED2D22BAFC295BEF8CD01471 B11AE06D3C4F151D33E84CFD6F0F4462153A5D69ED2D22BAFC295BEF8CD01471 B11AE06D3C4F151D33E84CFD6F0F4462153A5D69ED2D22BAFC295BEF8CD01471
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
-- @shoutech-migration 067_company_tax_foundation.sql 08E14163A6FC9A5E61A3DA88CAD3D5C05E3CBBF11DBD674C2D6EB0459022F6AF 269B45513FEB073B2AFF58AFABAF736F34AE24E20311249B7A730AF46E7F3E6D 269B45513FEB073B2AFF58AFABAF736F34AE24E20311249B7A730AF46E7F3E6D 269B45513FEB073B2AFF58AFABAF736F34AE24E20311249B7A730AF46E7F3E6D
-- 067 - Company-scoped, opt-in tax foundation (SQLite).
-- No country rates are seeded: a company must explicitly enable taxation
-- and approved, effective-dated rules must be configured before calculation.

CREATE TABLE IF NOT EXISTS company_tax_settings (
    company_code TEXT PRIMARY KEY REFERENCES companies(company_code),
    country_code TEXT NOT NULL,
    tax_enabled INTEGER NOT NULL DEFAULT 0 CHECK (tax_enabled IN (0, 1)),
    tax_scheme TEXT NOT NULL DEFAULT '',
    registration_number TEXT NOT NULL DEFAULT '',
    effective_from TEXT,
    updated_by TEXT,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS company_tax_rules (
    tax_rule_id INTEGER PRIMARY KEY AUTOINCREMENT,
    company_code TEXT NOT NULL REFERENCES companies(company_code),
    country_code TEXT NOT NULL,
    tax_code TEXT NOT NULL,
    tax_name TEXT NOT NULL,
    tax_type TEXT NOT NULL,
    tax_treatment TEXT NOT NULL DEFAULT 'Standard',
    rate NUMERIC NOT NULL CHECK (rate >= 0),
    calculation_mode TEXT NOT NULL DEFAULT 'Exclusive'
        CHECK (calculation_mode IN ('Exclusive', 'Inclusive')),
    is_compound INTEGER NOT NULL DEFAULT 0 CHECK (is_compound IN (0, 1)),
    is_recoverable INTEGER NOT NULL DEFAULT 1 CHECK (is_recoverable IN (0, 1)),
    output_account_code TEXT,
    input_account_code TEXT,
    effective_from TEXT NOT NULL,
    effective_to TEXT,
    is_default INTEGER NOT NULL DEFAULT 0 CHECK (is_default IN (0, 1)),
    is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (company_code, tax_code, effective_from)
);

CREATE INDEX IF NOT EXISTS ix_company_tax_rules_effective
    ON company_tax_rules(company_code, country_code, is_active, effective_from, effective_to);
CREATE INDEX IF NOT EXISTS ix_company_tax_rules_default
    ON company_tax_rules(company_code, is_default, is_active);
-- @shoutech-migration 068_invoice_line_tax_code.sql 567E3BF86A0DFB56A5458AC59DABB0DA40021079456BF7CF008CBF1D6A73E55C 9B4AD5B67B1ED73BD641CCBE87AA6B1B410FAB06966D9940BFF2659C6D898878 9B4AD5B67B1ED73BD641CCBE87AA6B1B410FAB06966D9940BFF2659C6D898878 9B4AD5B67B1ED73BD641CCBE87AA6B1B410FAB06966D9940BFF2659C6D898878
-- 068 - Persist the selected tax code on each commercial document line.
ALTER TABLE sales_document_lines ADD COLUMN tax_code TEXT NOT NULL DEFAULT '';
ALTER TABLE purchase_document_lines ADD COLUMN tax_code TEXT NOT NULL DEFAULT '';
CREATE INDEX IF NOT EXISTS ix_sales_document_lines_tax_code ON sales_document_lines(tax_code);
CREATE INDEX IF NOT EXISTS ix_purchase_document_lines_tax_code ON purchase_document_lines(tax_code);
-- @shoutech-migration 069_tax_transaction_register.sql C94CCA3C595FD896656EF0657085D0B6D6C1C2850F7B6AAC1E5CC6C62256B976 F535F06DA46D2043B575567B507885E4C8AD1238A04D9F95DA3C4F861F34ADEE F535F06DA46D2043B575567B507885E4C8AD1238A04D9F95DA3C4F861F34ADEE F535F06DA46D2043B575567B507885E4C8AD1238A04D9F95DA3C4F861F34ADEE
-- 069 - Immutable tax transaction register for posted commercial documents.
CREATE TABLE IF NOT EXISTS tax_transactions (
    tax_transaction_id INTEGER PRIMARY KEY AUTOINCREMENT,
    company_code TEXT NOT NULL,
    country_code TEXT NOT NULL,
    source_doc_kind TEXT NOT NULL,
    source_doc_id INTEGER NOT NULL,
    source_doc_no TEXT NOT NULL,
    line_no INTEGER NOT NULL,
    tax_code TEXT NOT NULL,
    tax_type TEXT NOT NULL,
    tax_treatment TEXT NOT NULL,
    tax_rate NUMERIC NOT NULL,
    taxable_amount NUMERIC NOT NULL,
    tax_amount NUMERIC NOT NULL,
    is_recoverable INTEGER NOT NULL CHECK (is_recoverable IN (0, 1)),
    tax_direction TEXT NOT NULL CHECK (tax_direction IN ('OUTPUT', 'INPUT')),
    tax_account_code TEXT NOT NULL DEFAULT '',
    currency_code TEXT NOT NULL,
    exchange_rate NUMERIC NOT NULL,
    base_tax_amount NUMERIC NOT NULL,
    journal_doc_no TEXT NOT NULL DEFAULT '',
    posted_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_by TEXT NOT NULL DEFAULT '',
    UNIQUE (company_code, source_doc_kind, source_doc_id, line_no, tax_code)
);
CREATE INDEX IF NOT EXISTS ix_tax_transactions_period
    ON tax_transactions(company_code, country_code, posted_at, tax_direction, tax_code);
CREATE INDEX IF NOT EXISTS ix_tax_transactions_source
    ON tax_transactions(company_code, source_doc_kind, source_doc_id);
-- @shoutech-migration 070_tax_rule_provenance.sql D94F20460BCF184D6CDB564EEB720FE6FF782FF6AE1AABEFCCCDF2D06537DB8E 5322ED43D02718417402A9488BDD33FBFD26649DECB3D463379BB5BA955F8378 5322ED43D02718417402A9488BDD33FBFD26649DECB3D463379BB5BA955F8378 5322ED43D02718417402A9488BDD33FBFD26649DECB3D463379BB5BA955F8378
-- 070 - Tax pack provenance and review status (SQLite).
-- @if-column-missing company_tax_rules.source_url
ALTER TABLE company_tax_rules ADD COLUMN source_url TEXT NOT NULL DEFAULT '';
-- @if-column-missing company_tax_rules.source_reference
ALTER TABLE company_tax_rules ADD COLUMN source_reference TEXT NOT NULL DEFAULT '';
-- @if-column-missing company_tax_rules.verified_on
ALTER TABLE company_tax_rules ADD COLUMN verified_on TEXT;
-- @if-column-missing company_tax_rules.data_status
ALTER TABLE company_tax_rules ADD COLUMN data_status TEXT NOT NULL DEFAULT 'USER_CONFIGURED';

CREATE INDEX IF NOT EXISTS ix_company_tax_rules_status
    ON company_tax_rules(company_code, country_code, data_status, is_active);
-- @shoutech-migration 071_tax_permissions.sql 09B528AC41CFCE6291C92319D4FD18EE0A86A99DE90D7FB42ADF36F6262D190E C719B94388F4FEA92281658940D361644CA2558297C738F483CF7455F9334A9E C719B94388F4FEA92281658940D361644CA2558297C738F483CF7455F9334A9E C719B94388F4FEA92281658940D361644CA2558297C738F483CF7455F9334A9E
-- 071 - Tax configuration and reporting permissions.
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES ('ACC.TAX.VIEW','Tax Configuration - View','ACCOUNTING','TAX','VIEW','Tax configuration access','Tax Configuration - View','View the company tax jurisdiction, rates, and tax accounts.',351,0);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='TAX',operation_code='VIEW',name_en='Tax Configuration - View',display_order=351,is_sensitive=0 WHERE permission_code='ACC.TAX.VIEW';

INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES ('ACC.TAX.MANAGE','Tax Configuration - Manage','ACCOUNTING','TAX','MANAGE','Tax configuration management','Tax Configuration - Manage','Create and maintain effective-dated tax rules and account mappings.',352,1);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='TAX',operation_code='MANAGE',name_en='Tax Configuration - Manage',display_order=352,is_sensitive=1 WHERE permission_code='ACC.TAX.MANAGE';

INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES ('ACC.TAX.REPORT.VIEW','Tax Reports - View','ACCOUNTING','TAX','REPORT_VIEW','Tax report access','Tax Reports - View','View tax transaction registers and tax period summaries.',353,0);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='TAX',operation_code='REPORT_VIEW',name_en='Tax Reports - View',display_order=353,is_sensitive=0 WHERE permission_code='ACC.TAX.REPORT.VIEW';

INSERT OR IGNORE INTO role_permissions(role_id,permission_id)
SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code='ROLE_ADMINISTRATOR' AND r.is_active=1 AND p.is_active=1
AND p.permission_code IN ('ACC.TAX.VIEW','ACC.TAX.MANAGE','ACC.TAX.REPORT.VIEW');
INSERT OR IGNORE INTO role_permissions(role_id,permission_id)
SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code='ROLE_USER' AND r.is_active=1 AND p.is_active=1
AND p.permission_code IN ('ACC.TAX.VIEW','ACC.TAX.REPORT.VIEW');
INSERT OR IGNORE INTO role_permissions(role_id,permission_id)
SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code='ROLE_VIEWER' AND r.is_active=1 AND p.is_active=1
AND p.permission_code IN ('ACC.TAX.VIEW','ACC.TAX.REPORT.VIEW');
-- @shoutech-migration 072_tax_document_date.sql F6E5933C93A909A1F1398B2F2D09325E98562222C65B0A9C98BD757DDD544CF8 E0B4B551AF6508003E1EC483C1F7CA861C4E2B4D1BDB6790F3C28DB25C01E990 E0B4B551AF6508003E1EC483C1F7CA861C4E2B4D1BDB6790F3C28DB25C01E990 E0B4B551AF6508003E1EC483C1F7CA861C4E2B4D1BDB6790F3C28DB25C01E990
-- 072 - Use the source document date for tax-period reporting.
-- @if-column-missing tax_transactions.document_date
ALTER TABLE tax_transactions ADD COLUMN document_date TEXT;
UPDATE tax_transactions
SET document_date = substr(posted_at, 1, 10)
WHERE document_date IS NULL OR document_date = '';
CREATE INDEX IF NOT EXISTS ix_tax_transactions_document_period
    ON tax_transactions(company_code, country_code, document_date, tax_direction, tax_code);
-- @shoutech-migration 073_tax_snapshot_immutability.sql 78D3D53AD34BBA156AABA55C899A6EB3852362068748BC3956698B79E046E9A1 D14D9CDD3EA9AAAA9612C843F50A3B1DE80A1074EB9DFA5E1715A2A43B5BA140 D14D9CDD3EA9AAAA9612C843F50A3B1DE80A1074EB9DFA5E1715A2A43B5BA140 D14D9CDD3EA9AAAA9612C843F50A3B1DE80A1074EB9DFA5E1715A2A43B5BA140
-- 073 - Enforce immutability of posted tax snapshots (SQLite).
CREATE TRIGGER IF NOT EXISTS trg_tax_transactions_no_update
BEFORE UPDATE ON tax_transactions
BEGIN
    SELECT RAISE(ABORT, 'Posted tax snapshots are immutable; create a reversal document.');
END;

CREATE TRIGGER IF NOT EXISTS trg_tax_transactions_no_delete
BEFORE DELETE ON tax_transactions
BEGIN
    SELECT RAISE(ABORT, 'Posted tax snapshots are immutable; create a reversal document.');
END;
-- @shoutech-migration 074_invoice_line_tax_calculation_mode.sql 29CF9ABD515398D969F94E75238D3E0A00800E901E4772A1E9C4EFC418095BAB D3D99694B1D26D9BC93BFC09E35B535FC44D0589D01D06DD653568FD3F5F896E D3D99694B1D26D9BC93BFC09E35B535FC44D0589D01D06DD653568FD3F5F896E D3D99694B1D26D9BC93BFC09E35B535FC44D0589D01D06DD653568FD3F5F896E
-- 074 - Persist per-line inclusive/exclusive tax calculation mode (SQLite).
-- @if-column-missing sales_document_lines.tax_calculation_mode
ALTER TABLE sales_document_lines
    ADD COLUMN tax_calculation_mode TEXT NOT NULL DEFAULT 'Exclusive'
    CHECK (tax_calculation_mode IN ('Exclusive', 'Inclusive'));
-- @if-column-missing purchase_document_lines.tax_calculation_mode
ALTER TABLE purchase_document_lines
    ADD COLUMN tax_calculation_mode TEXT NOT NULL DEFAULT 'Exclusive'
    CHECK (tax_calculation_mode IN ('Exclusive', 'Inclusive'));
-- @shoutech-migration 104_pos_remainder.sql 79261BB4B98E79D9872968D417ABEACEB68CC394FEFFCB1BC6BF1A3824903792 4A46A3D02DE67E404BD22FC841FE4F86442CC1E5EFEA0F49EBA1E6499C04723C 4A46A3D02DE67E404BD22FC841FE4F86442CC1E5EFEA0F49EBA1E6499C04723C 4A46A3D02DE67E404BD22FC841FE4F86442CC1E5EFEA0F49EBA1E6499C04723C
-- ============================================================================
-- 104 - POS remainder (SQLite) — sales cycle W9, owner spec §6 remainder
--
--   * pos_session_expenses: cash paid OUT of the drawer during an open
--     session (amount, reason, optional reference), recorded by a
--     POS.EXPENSE.MANAGE holder. Sessions post NO cash journal at
--     close today (066: CloseSessionAsync computes expected cash from
--     tenders and mirrors the legacy pos_cash_closings register), so a
--     paid-out follows that same reconciliation model — it is a
--     session record that SUBTRACTS from the expected cash in the Z
--     report and at close; no parallel GL posting is invented.
--   * pos_discount_limits: the NUMERIC cashier discount ceiling —
--     max discount % per USER (subject_key = username) or per ROLE
--     (subject_key = role_code). Resolution at the till: the USER row
--     wins, else the highest ROLE row among the cashier's roles, else
--     no numeric cap (the permission gate alone applies, as before).
--     Neither UsrPref (a per-machine UI preference file a cashier can
--     edit) nor the permissions catalog (boolean grants, no numeric
--     metadata) can own an enforced security limit, so the limits live
--     in this POS-owned table next to pos_settings (066).
--   * pos_discount_overrides: audit of a supervisor override — the
--     cashier, the approving supervisor (a POS.DISCOUNT.OVERRIDE
--     holder who authenticated at the till), the limit, the effective
--     and max-line discount %, and the discount amount, per document.
--     spr_price_overrides (099) does NOT fit: it is a per-line /
--     per-rule pricing-guard audit (SAL-7018/7019/7020) whose actor is
--     the poster and which has no approver distinct from the actor;
--     a POS discount override is a document-level, two-person event.
--   * sales_documents.exchange_ref: a POS exchange posts TWO existing
--     documents (an SRT return + a POS sale, each through the verified
--     InvRepo path in its own transaction); both carry the same
--     exchange_ref so the pair is one auditable flow.
--   * Permission POS.EXPENSE.MANAGE (sensitive; administrators by
--     default, grantable per user like the other POS rights).
--
-- Company is keyed by CODE (TEXT) — the party/POS world, like 066.
-- Idempotent: the checksum ledger skips a re-run; inside the file
-- every CREATE is IF NOT EXISTS, the ALTER is guarded by the migrator
-- directive, and permission seeds are INSERT OR IGNORE + UPDATE.
-- ============================================================================

-- ---- Session paid-outs -------------------------------------------------------
CREATE TABLE IF NOT EXISTS pos_session_expenses (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    session_id TEXT NOT NULL REFERENCES pos_sessions(id),
    expense_no TEXT NOT NULL,
    amount REAL NOT NULL DEFAULT 0,
    reason TEXT NOT NULL DEFAULT '',
    reference_no TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_pos_session_expenses_company_no
    ON pos_session_expenses(company_id, expense_no) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_pos_session_expenses_session
    ON pos_session_expenses(company_id, session_id) WHERE is_deleted = 0;

-- ---- Numeric cashier discount limits ------------------------------------------
CREATE TABLE IF NOT EXISTS pos_discount_limits (
    company_id TEXT NOT NULL,
    subject_kind TEXT NOT NULL CHECK (subject_kind IN ('USER','ROLE')),
    subject_key TEXT NOT NULL,
    max_discount_pct REAL NOT NULL DEFAULT 0,
    updated_by TEXT,
    updated_at TEXT,
    PRIMARY KEY (company_id, subject_kind, subject_key)
);

-- ---- Discount override audit ----------------------------------------------------
CREATE TABLE IF NOT EXISTS pos_discount_overrides (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    doc_kind TEXT,
    doc_no TEXT,
    doc_id INTEGER,
    cashier TEXT NOT NULL,
    approved_by TEXT NOT NULL,
    limit_pct REAL NOT NULL DEFAULT 0,
    effective_pct REAL NOT NULL DEFAULT 0,
    max_line_pct REAL NOT NULL DEFAULT 0,
    discount_amount REAL NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_pos_discount_overrides_doc
    ON pos_discount_overrides(company_id, doc_kind, doc_no);

-- ---- Exchange link ----------------------------------------------------------------
-- @if-column-missing sales_documents.exchange_ref
ALTER TABLE sales_documents ADD COLUMN exchange_ref TEXT;
CREATE INDEX IF NOT EXISTS ix_sales_documents_exchange_ref
    ON sales_documents(company_id, exchange_ref) WHERE is_deleted = 0;

-- ---- Permission: POS.EXPENSE.MANAGE -------------------------------------------------
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('POS.EXPENSE.MANAGE','POS Expense - Manage','POS','EXPENSE','MANAGE','مصروفات نقاط البيع - إدارة','POS Expense - Manage','Record cash paid out of the drawer during an open POS session (sales cycle W9, migration 104)',401,1);
UPDATE permissions SET group_code='POS',feature_code='EXPENSE',operation_code='MANAGE',name_ar='مصروفات نقاط البيع - إدارة',name_en='POS Expense - Manage',display_order=401,is_sensitive=1 WHERE permission_code='POS.EXPENSE.MANAGE';

INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('POS.EXPENSE.MANAGE');
-- @shoutech-migration 114_withholding_tax.sql D57D4076143D5DAD8EE05B7814DF7B995F410FFDB110DEB364E709A330463E6E E9C3D6AEBB6DD2AB4622C82F6C2423386BFA402D06E451B577CD3AFCBF6E12CF E9C3D6AEBB6DD2AB4622C82F6C2423386BFA402D06E451B577CD3AFCBF6E12CF E9C3D6AEBB6DD2AB4622C82F6C2423386BFA402D06E451B577CD3AFCBF6E12CF
-- 114 - Withholding tax / TDS (SQLite, W1). Withholding is a first-class
-- category of the EXISTING tax engine (migrations 067-091): a company tax
-- rule may be marked as a withholding rule, carrying its own rate, the
-- withholding PAYABLE (liability) control account it credits, and an
-- optional threshold. No country rates are seeded: every value is
-- configured per company / jurisdiction, exactly like the VAT rules.
-- PostgreSQL counterpart: postgresql/114_withholding_tax.sql.
--
-- Model (ERPNext invoice-time TDS): when a posted supplier invoice
-- carries a withholding rule, the invoice posts AP control 2201 GROSS,
-- then a deduction leg debits 2201 and credits the withholding payable
-- account, so the supplier's party-ledger balance and the document's
-- allocatable open amount are NET (gross - withheld). The withheld
-- amount is registered in tax_transactions with tax_type 'WITHHOLDING'
-- (direction INPUT, line_no 0) so the tax register is the single source
-- for withholding reporting / certificates.
--
-- Withholding base: the invoice base EXCLUDING VAT (document total
-- minus its tax amount on the manual path; the lines subtotal on the
-- chain path) - withholding is charged on the consideration for the
-- supply, never on the VAT the supplier collects for the authority.
-- Threshold modes: 'PerDocument' (the base of this one invoice must
-- reach the threshold) or 'Cumulative' (the supplier's calendar-year
-- withholding bases including this invoice must cross the threshold;
-- withholding then applies only to the excess over the threshold).

-- Withholding category on the existing tax-code model.
-- @if-column-missing company_tax_rules.is_withholding
ALTER TABLE company_tax_rules ADD COLUMN is_withholding INTEGER NOT NULL DEFAULT 0 CHECK (is_withholding IN (0, 1));
-- @if-column-missing company_tax_rules.withholding_account_code
ALTER TABLE company_tax_rules ADD COLUMN withholding_account_code TEXT;
-- @if-column-missing company_tax_rules.withholding_threshold_amount
ALTER TABLE company_tax_rules ADD COLUMN withholding_threshold_amount NUMERIC;
-- @if-column-missing company_tax_rules.withholding_threshold_mode
ALTER TABLE company_tax_rules ADD COLUMN withholding_threshold_mode TEXT NOT NULL DEFAULT 'PerDocument';

CREATE INDEX IF NOT EXISTS ix_company_tax_rules_withholding
    ON company_tax_rules(company_code, is_withholding, is_active);

-- Supplier-level default withholding code (party model default; the
-- invoice-level selection on purchase_documents wins over it, and the
-- company's default withholding rule is the final fallback).
-- @if-column-missing parties.withholding_tax_code
ALTER TABLE parties ADD COLUMN withholding_tax_code TEXT;

-- Persisted withholding outcome on the supplier invoice header: the
-- code/rate/base/amount/account actually applied at posting (0 amount
-- with a code set = evaluated, below threshold). payment allocation
-- settles grand_total - withholding_amount - paid_amount.
-- @if-column-missing purchase_documents.withholding_tax_code
ALTER TABLE purchase_documents ADD COLUMN withholding_tax_code TEXT;
-- @if-column-missing purchase_documents.withholding_rate
ALTER TABLE purchase_documents ADD COLUMN withholding_rate NUMERIC NOT NULL DEFAULT 0;
-- @if-column-missing purchase_documents.withholding_base
ALTER TABLE purchase_documents ADD COLUMN withholding_base NUMERIC NOT NULL DEFAULT 0;
-- @if-column-missing purchase_documents.withholding_amount
ALTER TABLE purchase_documents ADD COLUMN withholding_amount NUMERIC NOT NULL DEFAULT 0;
-- @if-column-missing purchase_documents.withholding_account_code
ALTER TABLE purchase_documents ADD COLUMN withholding_account_code TEXT;

CREATE INDEX IF NOT EXISTS ix_purchase_documents_withholding
    ON purchase_documents(company_id, party_id, doc_date) WHERE withholding_amount > 0;
CREATE INDEX IF NOT EXISTS ix_tax_transactions_withholding
    ON tax_transactions(company_code, tax_type, document_date, source_doc_id);

-- Withholding Tax Payable account (2215), the DEFAULT liability account
-- a withholding rule may point at (the rule's configured account is
-- authoritative; 2215 exists so the mechanism functions out of the box
-- and the payable is separately reportable). Seeded into every existing
-- company that carries AP control (2201), under 2201's parent in the
-- 22xx current-liability block (2201 AP, 2203 GRNI, 2205 Output VAT,
-- 2211 Payroll Taxes; 2215 is free in every template). New companies
-- receive it from the COA templates (cod/app/src/Resources/COA/*.shc).
INSERT INTO chart_of_accounts
    (account_id, company_id, account_code, account_name, name_ar, account_type,
     parent_account_id, is_postable, is_active, is_deleted)
SELECT lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' ||
       substr(lower(hex(randomblob(2))), 2) || '-a' ||
       substr(lower(hex(randomblob(2))), 2) || '-' || lower(hex(randomblob(6))),
       c.company_id,
       '2215',
       'Withholding Tax Payable',
       'Withholding Tax Payable',
       'LIABILITY',
       (SELECT p.parent_account_id FROM chart_of_accounts p
         WHERE p.company_id = c.company_id AND p.account_code = '2201'),
       1, 1, 0
FROM companies c
WHERE EXISTS (SELECT 1 FROM chart_of_accounts a
              WHERE a.company_id = c.company_id AND a.account_code = '2201')
  AND NOT EXISTS (SELECT 1 FROM chart_of_accounts a
                  WHERE a.company_id = c.company_id AND a.account_code = '2215');
-- @shoutech-migration 119_pos_terminal_definition.sql 08EBD8857F49CFFD23B84B408C4A81959796292955EC2198765208A1CF8C00AE EEB6CBDF6F822B73CFDB1D0C1C17A198F6EDBBBCB26B4490E4F07E1C1F59128E EEB6CBDF6F822B73CFDB1D0C1C17A198F6EDBBBCB26B4490E4F07E1C1F59128E EEB6CBDF6F822B73CFDB1D0C1C17A198F6EDBBBCB26B4490E4F07E1C1F59128E
-- 119 - POS terminal definition completion (POS family P1,
-- owner workstream 2026-11-24 15:31). pos_terminals (036)
-- carried only code, names, warehouse and the cash account.
-- A real terminal definition (Al-Ameen / Phenix / ERPNext POS
-- profile pattern) also binds the card/bank account, the cash
-- drawer, the receipt printer, the walk-in default customer,
-- the price level the till sells from, and the users permitted
-- to open sessions on the terminal.

-- @if-column-missing pos_terminals.bank_account_code
ALTER TABLE pos_terminals ADD COLUMN bank_account_code TEXT;
-- @if-column-missing pos_terminals.drawer_name
ALTER TABLE pos_terminals ADD COLUMN drawer_name TEXT;
-- @if-column-missing pos_terminals.printer_name
ALTER TABLE pos_terminals ADD COLUMN printer_name TEXT;
-- @if-column-missing pos_terminals.default_customer_code
ALTER TABLE pos_terminals ADD COLUMN default_customer_code TEXT;
-- @if-column-missing pos_terminals.price_level
ALTER TABLE pos_terminals ADD COLUMN price_level TEXT;
-- @if-column-missing pos_terminals.permitted_users
ALTER TABLE pos_terminals ADD COLUMN permitted_users TEXT;
-- @shoutech-migration 124_pos_terminal_p4.sql 3B0F69EB225C8890812A86F34795860CC64898B293F24B343BBE8588A045FE1D 0349F4BC7BD3AFDB3030271587BF398D229E6427EBCF9AC71AE89BA411D9CA88 0349F4BC7BD3AFDB3030271587BF398D229E6427EBCF9AC71AE89BA411D9CA88 0349F4BC7BD3AFDB3030271587BF398D229E6427EBCF9AC71AE89BA411D9CA88
-- 124 - POS terminal definition P4 (docs/POS_FAMILY.md P4): the
-- Phenix-verified bindings ShouTech lacked - the document kinds the
-- point issues, a SEPARATE cashbox-closing account, per-point
-- currency, customer-group scoping, and per-point print designs
-- (names of doc_designs rows, migration 116). Plus the closing
-- account recorded on each day-close register row.

-- @if-column-missing pos_terminals.sales_doc_kind
ALTER TABLE pos_terminals ADD COLUMN sales_doc_kind TEXT;
-- @if-column-missing pos_terminals.closing_account_code
ALTER TABLE pos_terminals ADD COLUMN closing_account_code TEXT;
-- @if-column-missing pos_terminals.currency_code
ALTER TABLE pos_terminals ADD COLUMN currency_code TEXT;
-- @if-column-missing pos_terminals.customer_group_code
ALTER TABLE pos_terminals ADD COLUMN customer_group_code TEXT;
-- @if-column-missing pos_terminals.sale_design_name
ALTER TABLE pos_terminals ADD COLUMN sale_design_name TEXT;
-- @if-column-missing pos_terminals.return_design_name
ALTER TABLE pos_terminals ADD COLUMN return_design_name TEXT;
-- @if-column-missing pos_terminals.gift_design_name
ALTER TABLE pos_terminals ADD COLUMN gift_design_name TEXT;
-- @if-column-missing pos_cash_closings.closing_account_code
ALTER TABLE pos_cash_closings ADD COLUMN closing_account_code TEXT;
