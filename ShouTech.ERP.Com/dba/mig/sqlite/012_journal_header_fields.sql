-- Persist the journal header fields already accepted by the domain and UI.
-- Defaults preserve compatibility with existing journal entries.
-- @if-column-missing journal_entries.reference_number
ALTER TABLE journal_entries ADD COLUMN reference_number TEXT NULL;
-- @if-column-missing journal_entries.currency_code
ALTER TABLE journal_entries ADD COLUMN currency_code TEXT NOT NULL DEFAULT 'SAR' CHECK (length(trim(currency_code)) = 3);
-- @if-column-missing journal_entries.exchange_rate
ALTER TABLE journal_entries ADD COLUMN exchange_rate NUMERIC NOT NULL DEFAULT 1 CHECK (exchange_rate > 0);
