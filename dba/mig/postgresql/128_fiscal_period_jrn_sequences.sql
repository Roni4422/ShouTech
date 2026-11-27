-- Central journal numbers restart at 1 for each actual fiscal period.
-- Keep the legacy document_number_sequences table for non-JRN families.
CREATE TABLE IF NOT EXISTS fiscal_period_number_sequences (
    company_id TEXT NOT NULL,
    series_key TEXT NOT NULL,
    fiscal_period_id UUID NOT NULL,
    last_value BIGINT NOT NULL CHECK (last_value >= 0),
    PRIMARY KEY (company_id, series_key, fiscal_period_id)
);
