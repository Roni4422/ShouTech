-- 070 - Tax pack provenance and review status (PostgreSQL).
ALTER TABLE company_tax_rules ADD COLUMN IF NOT EXISTS source_url text NOT NULL DEFAULT '';
ALTER TABLE company_tax_rules ADD COLUMN IF NOT EXISTS source_reference text NOT NULL DEFAULT '';
ALTER TABLE company_tax_rules ADD COLUMN IF NOT EXISTS verified_on date;
ALTER TABLE company_tax_rules ADD COLUMN IF NOT EXISTS data_status varchar(40) NOT NULL DEFAULT 'USER_CONFIGURED';

CREATE INDEX IF NOT EXISTS ix_company_tax_rules_status
    ON company_tax_rules(company_code, country_code, data_status, is_active);
