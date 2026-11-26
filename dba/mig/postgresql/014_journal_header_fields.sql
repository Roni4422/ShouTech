-- Persist the journal header fields already accepted by the domain and UI.
-- Defaults preserve compatibility with existing journal entries.
ALTER TABLE journal_entries ADD COLUMN IF NOT EXISTS reference_number varchar(100);
ALTER TABLE journal_entries ADD COLUMN IF NOT EXISTS currency_code char(3) NOT NULL DEFAULT 'SAR';
ALTER TABLE journal_entries ADD COLUMN IF NOT EXISTS exchange_rate numeric(19,8) NOT NULL DEFAULT 1 CHECK (exchange_rate > 0);
