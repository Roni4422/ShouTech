-- Migration 051 (SQLite): companies dual-store remediation (persistence
-- audit section 9.1). The company profile round-tripped ~30 fields through
-- companies.json while the database companies table (migration 001) had
-- only a handful of writable columns: TaxNumber had NO column at all (the
-- wizard wrote License into commercial_registration while the company-edit
-- path wrote Tax-first into the same column, so the two paths disagreed),
-- and Website/AddressLine2/City/Country/Notes/DecimalPlaces/CalendarType/
-- NumberGrouping/CoaTemplateCode had no columns either. This migration adds
-- the missing columns so every profile field maps to its own column
-- (License -> commercial_registration, TaxNumber -> tax_number).
-- Existing rows keep commercial_registration as-is: values written by the
-- create path are license numbers; a value written by the old edit path
-- cannot be distinguished reliably, so no backfill is attempted.
-- Runtime services must not create tables or columns; all DDL lives here
-- and in the PostgreSQL counterpart.

ALTER TABLE companies ADD COLUMN tax_number TEXT;
ALTER TABLE companies ADD COLUMN website TEXT;
ALTER TABLE companies ADD COLUMN address_line2 TEXT;
ALTER TABLE companies ADD COLUMN city TEXT;
ALTER TABLE companies ADD COLUMN country TEXT;
ALTER TABLE companies ADD COLUMN notes TEXT;
ALTER TABLE companies ADD COLUMN decimal_places INTEGER NOT NULL DEFAULT 2;
ALTER TABLE companies ADD COLUMN calendar_type TEXT NOT NULL DEFAULT 'Gregorian';
ALTER TABLE companies ADD COLUMN number_grouping TEXT NOT NULL DEFAULT '3-3-3';
ALTER TABLE companies ADD COLUMN coa_template_code TEXT NOT NULL DEFAULT 'SIMPLE';
