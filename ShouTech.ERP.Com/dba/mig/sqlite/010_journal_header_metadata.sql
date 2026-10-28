-- Persist the complete journal header entered by the journal window.
ALTER TABLE journal_entries ADD COLUMN reference_number TEXT NULL;
ALTER TABLE journal_entries ADD COLUMN currency_code TEXT NOT NULL DEFAULT 'SAR';
ALTER TABLE journal_entries ADD COLUMN exchange_rate NUMERIC NOT NULL DEFAULT 1 CHECK (exchange_rate > 0);
