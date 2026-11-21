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
