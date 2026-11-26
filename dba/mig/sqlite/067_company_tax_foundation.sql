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
