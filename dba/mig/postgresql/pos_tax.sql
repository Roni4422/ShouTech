-- @shoutech-migration 037_operations.sql E4E30653EF54D84448941B226C3BB1889E01A63D3F0B374BCC986195C09772B9 039F3F990FF4404892A3C6BA033985ABCC53B692F06FDBF654CE4238F93E6B25 039F3F990FF4404892A3C6BA033985ABCC53B692F06FDBF654CE4238F93E6B25 039F3F990FF4404892A3C6BA033985ABCC53B692F06FDBF654CE4238F93E6B25
-- Migration 037 (PostgreSQL): operations tables (ribbon completion RS-12).
-- Counterpart of sqlite/037_operations.sql.

CREATE TABLE IF NOT EXISTS pos_cash_closings (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    terminal_code TEXT NOT NULL,
    business_date TEXT NOT NULL,
    opened_by TEXT,
    closed_by TEXT,
    opening_amount DOUBLE PRECISION NOT NULL DEFAULT 0,
    expected_amount DOUBLE PRECISION NOT NULL DEFAULT 0,
    counted_amount DOUBLE PRECISION NOT NULL DEFAULT 0,
    difference DOUBLE PRECISION NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Closed',
    notes TEXT,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
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
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ,
    UNIQUE (company_id, ticket_no)
);

CREATE INDEX IF NOT EXISTS ix_support_tickets_company_kind
    ON support_tickets(company_id, kind, status);
-- @shoutech-migration 066_pos_sessions.sql 2755724BF71D4E1CE28BEA7D86BA3AD48442BC0BF26863F0473FE1EAC08B83A6 D639E2995D73C4F8DCDF089BD0047B3EDE0B508F7188B9A8F78F25D019760AFF D639E2995D73C4F8DCDF089BD0047B3EDE0B508F7188B9A8F78F25D019760AFF D639E2995D73C4F8DCDF089BD0047B3EDE0B508F7188B9A8F78F25D019760AFF
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
-- @shoutech-migration 067_company_tax_foundation.sql 2907155AD76A753C16105507E74C013A96B845F4D2143F5556ABBF77ABAF89E1 5F48FF534DC2497E9A8E3D22471FCDF0A8D86BD8708926D5DB0B292A3588E9E0 5F48FF534DC2497E9A8E3D22471FCDF0A8D86BD8708926D5DB0B292A3588E9E0 5F48FF534DC2497E9A8E3D22471FCDF0A8D86BD8708926D5DB0B292A3588E9E0
-- 067 - Company-scoped, opt-in tax foundation (PostgreSQL).
-- No country rates are seeded: a company must explicitly enable taxation
-- and approved, effective-dated rules must be configured before calculation.

CREATE TABLE IF NOT EXISTS company_tax_settings (
    company_code varchar(50) PRIMARY KEY REFERENCES companies(company_code),
    country_code char(2) NOT NULL,
    tax_enabled boolean NOT NULL DEFAULT false,
    tax_scheme varchar(80) NOT NULL DEFAULT '',
    registration_number varchar(120) NOT NULL DEFAULT '',
    effective_from date,
    updated_by varchar(100),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS company_tax_rules (
    tax_rule_id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    company_code varchar(50) NOT NULL REFERENCES companies(company_code),
    country_code char(2) NOT NULL,
    tax_code varchar(50) NOT NULL,
    tax_name varchar(200) NOT NULL,
    tax_type varchar(40) NOT NULL,
    tax_treatment varchar(40) NOT NULL DEFAULT 'Standard',
    rate numeric(12, 6) NOT NULL CHECK (rate >= 0),
    calculation_mode varchar(16) NOT NULL DEFAULT 'Exclusive'
        CHECK (calculation_mode IN ('Exclusive', 'Inclusive')),
    is_compound boolean NOT NULL DEFAULT false,
    is_recoverable boolean NOT NULL DEFAULT true,
    output_account_code varchar(50),
    input_account_code varchar(50),
    effective_from date NOT NULL,
    effective_to date,
    is_default boolean NOT NULL DEFAULT false,
    is_active boolean NOT NULL DEFAULT true,
    created_by varchar(100),
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (company_code, tax_code, effective_from)
);

CREATE INDEX IF NOT EXISTS ix_company_tax_rules_effective
    ON company_tax_rules(company_code, country_code, is_active, effective_from, effective_to);
CREATE INDEX IF NOT EXISTS ix_company_tax_rules_default
    ON company_tax_rules(company_code, is_default, is_active);
-- @shoutech-migration 068_invoice_line_tax_code.sql 654F594F77557459805AAF1BF01D0D06E2BE7F764EC89B0F1F9C195FE24513E5 1237E121ED4BFECB64718CC575F93A0A6583EFE6771B429C8993F2D8BEF42F98 1237E121ED4BFECB64718CC575F93A0A6583EFE6771B429C8993F2D8BEF42F98 1237E121ED4BFECB64718CC575F93A0A6583EFE6771B429C8993F2D8BEF42F98
-- 068 - Persist the selected tax code on each commercial document line.
ALTER TABLE sales_document_lines ADD COLUMN IF NOT EXISTS tax_code varchar(50) NOT NULL DEFAULT '';
ALTER TABLE purchase_document_lines ADD COLUMN IF NOT EXISTS tax_code varchar(50) NOT NULL DEFAULT '';
CREATE INDEX IF NOT EXISTS ix_sales_document_lines_tax_code ON sales_document_lines(tax_code);
CREATE INDEX IF NOT EXISTS ix_purchase_document_lines_tax_code ON purchase_document_lines(tax_code);
-- @shoutech-migration 069_tax_transaction_register.sql 795A4E00C19DB5415C8D465DE016B57A7AEB347EAB70642A804F5CAE7B79F973 E5327A32DCEBD640DDEAAECE9A61726C3E644B888EC6005CB789826694FE0113 E5327A32DCEBD640DDEAAECE9A61726C3E644B888EC6005CB789826694FE0113 E5327A32DCEBD640DDEAAECE9A61726C3E644B888EC6005CB789826694FE0113
-- 069 - Immutable tax transaction register for posted commercial documents.
CREATE TABLE IF NOT EXISTS tax_transactions (
    tax_transaction_id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    company_code varchar(50) NOT NULL,
    country_code char(2) NOT NULL,
    source_doc_kind varchar(20) NOT NULL,
    source_doc_id bigint NOT NULL,
    source_doc_no varchar(80) NOT NULL,
    line_no integer NOT NULL,
    tax_code varchar(50) NOT NULL,
    tax_type varchar(40) NOT NULL,
    tax_treatment varchar(40) NOT NULL,
    tax_rate numeric(12,6) NOT NULL,
    taxable_amount numeric(20,6) NOT NULL,
    tax_amount numeric(20,6) NOT NULL,
    is_recoverable boolean NOT NULL,
    tax_direction varchar(8) NOT NULL CHECK (tax_direction IN ('OUTPUT', 'INPUT')),
    tax_account_code varchar(50) NOT NULL DEFAULT '',
    currency_code varchar(3) NOT NULL,
    exchange_rate numeric(20,8) NOT NULL,
    base_tax_amount numeric(20,6) NOT NULL,
    journal_doc_no varchar(80) NOT NULL DEFAULT '',
    posted_at timestamptz NOT NULL DEFAULT now(),
    created_by varchar(100) NOT NULL DEFAULT '',
    UNIQUE (company_code, source_doc_kind, source_doc_id, line_no, tax_code)
);
CREATE INDEX IF NOT EXISTS ix_tax_transactions_period
    ON tax_transactions(company_code, country_code, posted_at, tax_direction, tax_code);
CREATE INDEX IF NOT EXISTS ix_tax_transactions_source
    ON tax_transactions(company_code, source_doc_kind, source_doc_id);
-- @shoutech-migration 070_tax_rule_provenance.sql 00C511F9542CFC0BAE6CE9A6768AE223975DB1010A4F7AA04A4146E7E695EB3B 602A55050174A31F0E746CBF37F9FF50E49A5721094EC4076835D65CE3FF3364 602A55050174A31F0E746CBF37F9FF50E49A5721094EC4076835D65CE3FF3364 602A55050174A31F0E746CBF37F9FF50E49A5721094EC4076835D65CE3FF3364
-- 070 - Tax pack provenance and review status (PostgreSQL).
ALTER TABLE company_tax_rules ADD COLUMN IF NOT EXISTS source_url text NOT NULL DEFAULT '';
ALTER TABLE company_tax_rules ADD COLUMN IF NOT EXISTS source_reference text NOT NULL DEFAULT '';
ALTER TABLE company_tax_rules ADD COLUMN IF NOT EXISTS verified_on date;
ALTER TABLE company_tax_rules ADD COLUMN IF NOT EXISTS data_status varchar(40) NOT NULL DEFAULT 'USER_CONFIGURED';

CREATE INDEX IF NOT EXISTS ix_company_tax_rules_status
    ON company_tax_rules(company_code, country_code, data_status, is_active);
-- @shoutech-migration 071_tax_permissions.sql ED79B2135D371BFB5EF793BFB9855229966BCC355527413171BC817CA65FE7E0 32C2BBB56D539E13562CE0B05E954037E55323B330768C080ED2DE9C703459B9 32C2BBB56D539E13562CE0B05E954037E55323B330768C080ED2DE9C703459B9 32C2BBB56D539E13562CE0B05E954037E55323B330768C080ED2DE9C703459B9
-- 071 - Tax configuration and reporting permissions.
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES ('ACC.TAX.VIEW','Tax Configuration - View','ACCOUNTING','TAX','VIEW','Tax configuration access','Tax Configuration - View','View the company tax jurisdiction, rates, and tax accounts.',351,false)
ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES ('ACC.TAX.MANAGE','Tax Configuration - Manage','ACCOUNTING','TAX','MANAGE','Tax configuration management','Tax Configuration - Manage','Create and maintain effective-dated tax rules and account mappings.',352,true)
ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES ('ACC.TAX.REPORT.VIEW','Tax Reports - View','ACCOUNTING','TAX','REPORT_VIEW','Tax report access','Tax Reports - View','View tax transaction registers and tax period summaries.',353,false)
ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;

INSERT INTO role_permissions(role_id,permission_id)
SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code='ROLE_ADMINISTRATOR' AND r.is_active=true AND p.is_active=true
AND p.permission_code IN ('ACC.TAX.VIEW','ACC.TAX.MANAGE','ACC.TAX.REPORT.VIEW') ON CONFLICT DO NOTHING;
INSERT INTO role_permissions(role_id,permission_id)
SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code='ROLE_USER' AND r.is_active=true AND p.is_active=true
AND p.permission_code IN ('ACC.TAX.VIEW','ACC.TAX.REPORT.VIEW') ON CONFLICT DO NOTHING;
INSERT INTO role_permissions(role_id,permission_id)
SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code='ROLE_VIEWER' AND r.is_active=true AND p.is_active=true
AND p.permission_code IN ('ACC.TAX.VIEW','ACC.TAX.REPORT.VIEW') ON CONFLICT DO NOTHING;
-- @shoutech-migration 072_tax_document_date.sql 4A26F39FA4876C52283607E422DB7E5AC407F2505AA2EFA4429BC6431DA89FC4 00601684B5FEA1DC1DF253215D155E3B7CAAFBD3A6F7B1F3EABE7B949D438DF4 00601684B5FEA1DC1DF253215D155E3B7CAAFBD3A6F7B1F3EABE7B949D438DF4 00601684B5FEA1DC1DF253215D155E3B7CAAFBD3A6F7B1F3EABE7B949D438DF4
-- 072 - Use the source document date for tax-period reporting.
ALTER TABLE tax_transactions ADD COLUMN IF NOT EXISTS document_date date;
UPDATE tax_transactions
SET document_date = posted_at::date
WHERE document_date IS NULL;
CREATE INDEX IF NOT EXISTS ix_tax_transactions_document_period
    ON tax_transactions(company_code, country_code, document_date, tax_direction, tax_code);
-- @shoutech-migration 073_tax_snapshot_immutability.sql 3D2422E197C5595F3E3AD542ABAB8DB42904BFD21B748C25F0FC8B0FA1A7C8EA 5ECBDB0420154B5FDCB08245E4414EDE92E436B75033D74C39C9E89E9002B8E4 5ECBDB0420154B5FDCB08245E4414EDE92E436B75033D74C39C9E89E9002B8E4 5ECBDB0420154B5FDCB08245E4414EDE92E436B75033D74C39C9E89E9002B8E4
-- 073 - Enforce immutability of posted tax snapshots (PostgreSQL).
CREATE OR REPLACE FUNCTION prevent_tax_transaction_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $tax_guard$
BEGIN
    RAISE EXCEPTION 'Posted tax snapshots are immutable; create a reversal document.';
END;
$tax_guard$;

DROP TRIGGER IF EXISTS trg_tax_transactions_no_update ON tax_transactions;
CREATE TRIGGER trg_tax_transactions_no_update
BEFORE UPDATE ON tax_transactions
FOR EACH ROW EXECUTE FUNCTION prevent_tax_transaction_mutation();

DROP TRIGGER IF EXISTS trg_tax_transactions_no_delete ON tax_transactions;
CREATE TRIGGER trg_tax_transactions_no_delete
BEFORE DELETE ON tax_transactions
FOR EACH ROW EXECUTE FUNCTION prevent_tax_transaction_mutation();
-- @shoutech-migration 074_invoice_line_tax_calculation_mode.sql 532508373AF9A936F5EB984C3C8C671CB991B0780C13A8343A79C326B68367EB AA54A3C5B8E637B8B825484AFF602F40476F339571FA1495A23DC77187FFADC1 AA54A3C5B8E637B8B825484AFF602F40476F339571FA1495A23DC77187FFADC1 AA54A3C5B8E637B8B825484AFF602F40476F339571FA1495A23DC77187FFADC1
-- 074 - Persist per-line inclusive/exclusive tax calculation mode (PostgreSQL).
ALTER TABLE sales_document_lines
    ADD COLUMN IF NOT EXISTS tax_calculation_mode varchar(16) NOT NULL DEFAULT 'Exclusive';
ALTER TABLE purchase_document_lines
    ADD COLUMN IF NOT EXISTS tax_calculation_mode varchar(16) NOT NULL DEFAULT 'Exclusive';

DO $tax_mode$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='ck_sales_line_tax_calculation_mode') THEN
        ALTER TABLE sales_document_lines
            ADD CONSTRAINT ck_sales_line_tax_calculation_mode
            CHECK (tax_calculation_mode IN ('Exclusive', 'Inclusive'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='ck_purchase_line_tax_calculation_mode') THEN
        ALTER TABLE purchase_document_lines
            ADD CONSTRAINT ck_purchase_line_tax_calculation_mode
            CHECK (tax_calculation_mode IN ('Exclusive', 'Inclusive'));
    END IF;
END;
$tax_mode$;
-- @shoutech-migration 104_pos_remainder.sql 2C2501E12803A60617A806D2BC0F4ABA8636E0472C121CE7BD8C410F2D4DE568 B38C76E1B7267F184899F6B9658AEC7A6532729554561595D1BFBBF5241B6B48 B38C76E1B7267F184899F6B9658AEC7A6532729554561595D1BFBBF5241B6B48 B38C76E1B7267F184899F6B9658AEC7A6532729554561595D1BFBBF5241B6B48
-- ============================================================================
-- 104 - POS remainder (PostgreSQL)
--
-- PostgreSQL twin of dba/mig/sqlite/104_pos_remainder.sql — see that
-- file for the full design notes (session paid-outs subtracted from
-- expected cash, numeric per-user/per-role cashier discount limits,
-- the two-person discount-override audit, and the exchange_ref link
-- between an exchange's SRT return and POS sale). Dialect differences
-- only: NUMERIC money/percentages, BOOLEAN flags, TIMESTAMPTZ audit
-- stamps, ADD COLUMN IF NOT EXISTS, ON CONFLICT seed upserts.
-- ============================================================================

-- ---- Session paid-outs -------------------------------------------------------
CREATE TABLE IF NOT EXISTS pos_session_expenses (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    session_id TEXT NOT NULL REFERENCES pos_sessions(id),
    expense_no TEXT NOT NULL,
    amount NUMERIC NOT NULL DEFAULT 0,
    reason TEXT NOT NULL DEFAULT '',
    reference_no TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_pos_session_expenses_company_no
    ON pos_session_expenses(company_id, expense_no) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_pos_session_expenses_session
    ON pos_session_expenses(company_id, session_id) WHERE is_deleted = FALSE;

-- ---- Numeric cashier discount limits ------------------------------------------
CREATE TABLE IF NOT EXISTS pos_discount_limits (
    company_id TEXT NOT NULL,
    subject_kind TEXT NOT NULL CHECK (subject_kind IN ('USER','ROLE')),
    subject_key TEXT NOT NULL,
    max_discount_pct NUMERIC NOT NULL DEFAULT 0,
    updated_by TEXT,
    updated_at TIMESTAMPTZ,
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
    limit_pct NUMERIC NOT NULL DEFAULT 0,
    effective_pct NUMERIC NOT NULL DEFAULT 0,
    max_line_pct NUMERIC NOT NULL DEFAULT 0,
    discount_amount NUMERIC NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_pos_discount_overrides_doc
    ON pos_discount_overrides(company_id, doc_kind, doc_no);

-- ---- Exchange link ----------------------------------------------------------------
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS exchange_ref TEXT;
CREATE INDEX IF NOT EXISTS ix_sales_documents_exchange_ref
    ON sales_documents(company_id, exchange_ref) WHERE is_deleted = 0;

-- ---- Permission: POS.EXPENSE.MANAGE -------------------------------------------------
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('POS.EXPENSE.MANAGE','POS Expense - Manage','POS','EXPENSE','MANAGE','مصروفات نقاط البيع - إدارة','POS Expense - Manage','Record cash paid out of the drawer during an open POS session (sales cycle W9, migration 104)',401,true) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('POS.EXPENSE.MANAGE') ON CONFLICT DO NOTHING;
-- @shoutech-migration 114_withholding_tax.sql 6E533AB28D197A1FDE999E5385E3AB804F607D832CFD65A1746F06A4BFBBAF02 7AA701BE7BABA75B5959D167D2F0D4C075FD7FB88F69CAA8BEF64B59A7A1B944 7AA701BE7BABA75B5959D167D2F0D4C075FD7FB88F69CAA8BEF64B59A7A1B944 7AA701BE7BABA75B5959D167D2F0D4C075FD7FB88F69CAA8BEF64B59A7A1B944
-- 114 - Withholding tax / TDS (PostgreSQL, W1). See the SQLite
-- companion for the design note: withholding as a category of the
-- existing tax engine, deducted at supplier-invoice posting, AP gross
-- with a deduction leg to the withholding payable account, supplier
-- balance and allocation NET, tax_transactions as the only register.

-- @if-column-missing company_tax_rules.is_withholding
ALTER TABLE company_tax_rules ADD COLUMN is_withholding BOOLEAN NOT NULL DEFAULT FALSE;
-- @if-column-missing company_tax_rules.withholding_account_code
ALTER TABLE company_tax_rules ADD COLUMN withholding_account_code VARCHAR(50);
-- @if-column-missing company_tax_rules.withholding_threshold_amount
ALTER TABLE company_tax_rules ADD COLUMN withholding_threshold_amount NUMERIC(20,6);
-- @if-column-missing company_tax_rules.withholding_threshold_mode
ALTER TABLE company_tax_rules ADD COLUMN withholding_threshold_mode VARCHAR(20) NOT NULL DEFAULT 'PerDocument';

CREATE INDEX IF NOT EXISTS ix_company_tax_rules_withholding
    ON company_tax_rules(company_code, is_withholding, is_active);

-- @if-column-missing parties.withholding_tax_code
ALTER TABLE parties ADD COLUMN withholding_tax_code VARCHAR(50);

-- @if-column-missing purchase_documents.withholding_tax_code
ALTER TABLE purchase_documents ADD COLUMN withholding_tax_code VARCHAR(50);
-- @if-column-missing purchase_documents.withholding_rate
ALTER TABLE purchase_documents ADD COLUMN withholding_rate NUMERIC(20,6) NOT NULL DEFAULT 0;
-- @if-column-missing purchase_documents.withholding_base
ALTER TABLE purchase_documents ADD COLUMN withholding_base NUMERIC(20,6) NOT NULL DEFAULT 0;
-- @if-column-missing purchase_documents.withholding_amount
ALTER TABLE purchase_documents ADD COLUMN withholding_amount NUMERIC(20,6) NOT NULL DEFAULT 0;
-- @if-column-missing purchase_documents.withholding_account_code
ALTER TABLE purchase_documents ADD COLUMN withholding_account_code VARCHAR(50);

CREATE INDEX IF NOT EXISTS ix_purchase_documents_withholding
    ON purchase_documents(company_id, party_id, doc_date) WHERE withholding_amount > 0;
CREATE INDEX IF NOT EXISTS ix_tax_transactions_withholding
    ON tax_transactions(company_code, tax_type, document_date, source_doc_id);

-- Withholding Tax Payable (2215): see the SQLite companion.
INSERT INTO chart_of_accounts
    (account_id, company_id, account_code, account_name, name_ar, account_type,
     parent_account_id, is_postable, is_active, is_deleted)
SELECT md5(c.company_id::text || ':114:' || '2215')::uuid,
       c.company_id,
       '2215',
       'Withholding Tax Payable',
       'Withholding Tax Payable',
       'LIABILITY',
       (SELECT p.parent_account_id FROM chart_of_accounts p
         WHERE p.company_id = c.company_id AND p.account_code = '2201'),
       TRUE, TRUE, FALSE
FROM companies c
WHERE EXISTS (SELECT 1 FROM chart_of_accounts a
              WHERE a.company_id = c.company_id AND a.account_code = '2201')
ON CONFLICT (company_id, account_code) DO NOTHING;
-- @shoutech-migration 119_pos_terminal_definition.sql F56147CC4D8E287F19FAEA128B4262F94AA89A939646F4179E714EED556F4767 BB0B0582A4461B16B670925FA75B7E926320E336C381D21EF15B37A8FEBEE087 BB0B0582A4461B16B670925FA75B7E926320E336C381D21EF15B37A8FEBEE087 BB0B0582A4461B16B670925FA75B7E926320E336C381D21EF15B37A8FEBEE087
-- 119 - POS terminal definition completion (POS family P1,
-- owner workstream 2026-11-24 15:31). pos_terminals (036)
-- carried only code, names, warehouse and the cash account.
-- A real terminal definition (Al-Ameen / Phenix / ERPNext POS
-- profile pattern) also binds the card/bank account, the cash
-- drawer, the receipt printer, the walk-in default customer,
-- the price level the till sells from, and the users permitted
-- to open sessions on the terminal.

-- @if-column-missing pos_terminals.bank_account_code
ALTER TABLE pos_terminals ADD COLUMN bank_account_code VARCHAR(50);
-- @if-column-missing pos_terminals.drawer_name
ALTER TABLE pos_terminals ADD COLUMN drawer_name VARCHAR(100);
-- @if-column-missing pos_terminals.printer_name
ALTER TABLE pos_terminals ADD COLUMN printer_name VARCHAR(100);
-- @if-column-missing pos_terminals.default_customer_code
ALTER TABLE pos_terminals ADD COLUMN default_customer_code VARCHAR(50);
-- @if-column-missing pos_terminals.price_level
ALTER TABLE pos_terminals ADD COLUMN price_level VARCHAR(50);
-- @if-column-missing pos_terminals.permitted_users
ALTER TABLE pos_terminals ADD COLUMN permitted_users TEXT;
-- @shoutech-migration 124_pos_terminal_p4.sql 436B97F5A299752ECBD9E57A7BF8130A6CD2BD896707420757DBFF1E025BFE35 5D56319BA0BFD3F34805D24ED575B226C227E78538F2B343625DF5A620B7AC0E 5D56319BA0BFD3F34805D24ED575B226C227E78538F2B343625DF5A620B7AC0E 5D56319BA0BFD3F34805D24ED575B226C227E78538F2B343625DF5A620B7AC0E
-- 124 - POS terminal definition P4 (docs/POS_FAMILY.md P4): the
-- Phenix-verified bindings ShouTech lacked - the document kinds the
-- point issues, a SEPARATE cashbox-closing account, per-point
-- currency, customer-group scoping, and per-point print designs
-- (names of doc_designs rows, migration 116). Plus the closing
-- account recorded on each day-close register row.

-- @if-column-missing pos_terminals.sales_doc_kind
ALTER TABLE pos_terminals ADD COLUMN sales_doc_kind VARCHAR(50);
-- @if-column-missing pos_terminals.closing_account_code
ALTER TABLE pos_terminals ADD COLUMN closing_account_code VARCHAR(50);
-- @if-column-missing pos_terminals.currency_code
ALTER TABLE pos_terminals ADD COLUMN currency_code VARCHAR(10);
-- @if-column-missing pos_terminals.customer_group_code
ALTER TABLE pos_terminals ADD COLUMN customer_group_code VARCHAR(50);
-- @if-column-missing pos_terminals.sale_design_name
ALTER TABLE pos_terminals ADD COLUMN sale_design_name VARCHAR(200);
-- @if-column-missing pos_terminals.return_design_name
ALTER TABLE pos_terminals ADD COLUMN return_design_name VARCHAR(200);
-- @if-column-missing pos_terminals.gift_design_name
ALTER TABLE pos_terminals ADD COLUMN gift_design_name VARCHAR(200);
-- @if-column-missing pos_cash_closings.closing_account_code
ALTER TABLE pos_cash_closings ADD COLUMN closing_account_code VARCHAR(50);
