-- Central journal numbers restart at 1 for each actual fiscal period.
-- Keep the legacy document_number_sequences table for non-JRN families.
CREATE TABLE IF NOT EXISTS fiscal_period_number_sequences (
    company_id TEXT NOT NULL,
    series_key TEXT NOT NULL,
    fiscal_period_id UUID NOT NULL,
    last_value BIGINT NOT NULL CHECK (last_value >= 0),
    PRIMARY KEY (company_id, series_key, fiscal_period_id)
);

-- Allow the same immutable JRN number in different fiscal periods.
DROP INDEX IF EXISTS ux_journal_entries_company_document_number;
CREATE UNIQUE INDEX IF NOT EXISTS ux_journal_entries_company_period_document_number
    ON journal_entries(company_id, fiscal_period_id, document_number)
    WHERE document_number IS NOT NULL AND length(trim(document_number)) > 0;
