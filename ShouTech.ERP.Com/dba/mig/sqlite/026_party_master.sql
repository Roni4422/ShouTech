CREATE TABLE IF NOT EXISTS parties (
    party_id INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL REFERENCES companies(company_id),
    party_code TEXT NOT NULL,
    party_kind TEXT NOT NULL CHECK (party_kind IN ('C', 'S', 'B')),
    name_en TEXT NOT NULL,
    name_ar TEXT,
    group_code TEXT,
    tax_number TEXT,
    commercial_registration TEXT,
    credit_limit NUMERIC NOT NULL DEFAULT 0 CHECK (credit_limit >= 0),
    credit_days INTEGER NOT NULL DEFAULT 0 CHECK (credit_days >= 0),
    price_tier TEXT,
    gl_account_code TEXT,
    phone TEXT,
    mobile TEXT,
    email TEXT,
    address TEXT,
    is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    is_deleted INTEGER NOT NULL DEFAULT 0 CHECK (is_deleted IN (0, 1)),
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_by TEXT,
    updated_at TEXT,
    UNIQUE (company_id, party_kind, party_code)
);

CREATE INDEX IF NOT EXISTS ix_parties_company_kind_active
    ON parties(company_id, party_kind, is_active, is_deleted);

CREATE INDEX IF NOT EXISTS ix_parties_company_name
    ON parties(company_id, name_en COLLATE NOCASE);

CREATE INDEX IF NOT EXISTS ix_parties_company_tax
    ON parties(company_id, tax_number)
    WHERE tax_number IS NOT NULL;
