-- Migration 051 (PostgreSQL): counterpart of sqlite/051_companies_profile_columns.sql.
-- Companies dual-store remediation (persistence audit section 9.1): adds the
-- profile columns the database companies table was missing so every company
-- field maps to its own column (License -> commercial_registration,
-- TaxNumber -> tax_number), ending the Tax/License conflation between the
-- create and edit write paths. No backfill: existing commercial_registration
-- values written by the old edit path cannot be distinguished reliably.

ALTER TABLE companies ADD COLUMN IF NOT EXISTS tax_number varchar(100);
ALTER TABLE companies ADD COLUMN IF NOT EXISTS website varchar(250);
ALTER TABLE companies ADD COLUMN IF NOT EXISTS address_line2 varchar(500);
ALTER TABLE companies ADD COLUMN IF NOT EXISTS city varchar(100);
ALTER TABLE companies ADD COLUMN IF NOT EXISTS country varchar(100);
ALTER TABLE companies ADD COLUMN IF NOT EXISTS notes text;
ALTER TABLE companies ADD COLUMN IF NOT EXISTS decimal_places integer NOT NULL DEFAULT 2;
ALTER TABLE companies ADD COLUMN IF NOT EXISTS calendar_type varchar(20) NOT NULL DEFAULT 'Gregorian';
ALTER TABLE companies ADD COLUMN IF NOT EXISTS number_grouping varchar(20) NOT NULL DEFAULT '3-3-3';
ALTER TABLE companies ADD COLUMN IF NOT EXISTS coa_template_code varchar(50) NOT NULL DEFAULT 'SIMPLE';
