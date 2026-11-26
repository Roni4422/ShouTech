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
