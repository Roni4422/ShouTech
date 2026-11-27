-- ============================================================================
-- 095 - Customer management completion (PostgreSQL)
-- Mirror of sqlite/095_party_structure.sql: party contacts/addresses/
-- branches, party header additions (default currency, risk class,
-- blacklist, opening-balance marker), sales_documents.due_date, the
-- credit_overrides audit table, and the SALES.CREDIT.OVERRIDE seed.
-- Idempotent: CREATE TABLE IF NOT EXISTS, ADD COLUMN IF NOT EXISTS,
-- permission upsert ON CONFLICT.
-- ============================================================================

CREATE TABLE IF NOT EXISTS party_contacts (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    party_id UUID NOT NULL REFERENCES parties(id),
    name TEXT NOT NULL,
    role TEXT,
    phone TEXT,
    mobile TEXT,
    email TEXT,
    is_primary BOOLEAN NOT NULL DEFAULT false,
    is_active BOOLEAN NOT NULL DEFAULT true,
    notes TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT false,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (now()::text),
    updated_by TEXT,
    updated_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_party_contacts_party
    ON party_contacts(company_id, party_id) WHERE is_deleted = false;

CREATE TABLE IF NOT EXISTS party_addresses (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    party_id UUID NOT NULL REFERENCES parties(id),
    kind TEXT NOT NULL DEFAULT 'Delivery' CHECK (kind IN ('Delivery','Billing','Other')),
    city TEXT,
    country TEXT,
    postal_code TEXT,
    address TEXT,
    is_default BOOLEAN NOT NULL DEFAULT false,
    is_active BOOLEAN NOT NULL DEFAULT true,
    notes TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT false,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (now()::text),
    updated_by TEXT,
    updated_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_party_addresses_party
    ON party_addresses(company_id, party_id) WHERE is_deleted = false;

CREATE TABLE IF NOT EXISTS party_branches (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    party_id UUID NOT NULL REFERENCES parties(id),
    name TEXT NOT NULL,
    address TEXT,
    phone TEXT,
    is_active BOOLEAN NOT NULL DEFAULT true,
    notes TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT false,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (now()::text),
    updated_by TEXT,
    updated_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_party_branches_party
    ON party_branches(company_id, party_id) WHERE is_deleted = false;

CREATE TABLE IF NOT EXISTS credit_overrides (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    party_kind TEXT NOT NULL,
    party_id BIGINT NOT NULL,
    doc_kind TEXT,
    doc_no TEXT,
    current_balance NUMERIC NOT NULL DEFAULT 0,
    document_amount NUMERIC NOT NULL DEFAULT 0,
    credit_limit NUMERIC NOT NULL DEFAULT 0,
    actor TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (now()::text)
);

CREATE INDEX IF NOT EXISTS ix_credit_overrides_party
    ON credit_overrides(company_id, party_kind, party_id);

ALTER TABLE parties ADD COLUMN IF NOT EXISTS default_currency TEXT;
ALTER TABLE parties ADD COLUMN IF NOT EXISTS risk_class TEXT;
ALTER TABLE parties ADD COLUMN IF NOT EXISTS is_blacklisted BOOLEAN NOT NULL DEFAULT false;
ALTER TABLE parties ADD COLUMN IF NOT EXISTS blacklist_reason TEXT;
ALTER TABLE parties ADD COLUMN IF NOT EXISTS blacklisted_at TEXT;
ALTER TABLE parties ADD COLUMN IF NOT EXISTS blacklisted_by TEXT;
ALTER TABLE parties ADD COLUMN IF NOT EXISTS opening_balance_amount NUMERIC;
ALTER TABLE parties ADD COLUMN IF NOT EXISTS opening_balance_date TEXT;
ALTER TABLE parties ADD COLUMN IF NOT EXISTS opening_journal_id TEXT;

ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS due_date TEXT;

INSERT INTO permissions
    (permission_code, permission_name, group_code, feature_code, operation_code,
     name_ar, name_en, description, display_order, is_sensitive)
VALUES
    ('SALES.CREDIT.OVERRIDE', 'Sales - Override Credit Limit',
     'SALES', 'CREDIT', 'OVERRIDE',
     'المبيعات - تجاوز حد الائتمان',
     'Sales - Override Credit Limit',
     'Post a sales document past the party credit limit; every override is audited', 344, true)
ON CONFLICT(permission_code) DO UPDATE SET
    permission_name=EXCLUDED.permission_name,
    group_code=EXCLUDED.group_code,
    feature_code=EXCLUDED.feature_code,
    operation_code=EXCLUDED.operation_code,
    name_ar=EXCLUDED.name_ar,
    name_en=EXCLUDED.name_en,
    description=EXCLUDED.description,
    display_order=EXCLUDED.display_order,
    is_sensitive=EXCLUDED.is_sensitive,
    is_active=true;

INSERT INTO role_permissions(role_id, permission_id)
SELECT r.role_id, p.permission_id
FROM roles r CROSS JOIN permissions p
WHERE r.role_code='ROLE_ADMINISTRATOR' AND r.is_active=true AND p.is_active=true
  AND p.permission_code='SALES.CREDIT.OVERRIDE'
ON CONFLICT DO NOTHING;
