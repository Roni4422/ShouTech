-- 072 - Use the source document date for tax-period reporting.
ALTER TABLE tax_transactions ADD COLUMN IF NOT EXISTS document_date date;
UPDATE tax_transactions
SET document_date = posted_at::date
WHERE document_date IS NULL;
CREATE INDEX IF NOT EXISTS ix_tax_transactions_document_period
    ON tax_transactions(company_code, country_code, document_date, tax_direction, tax_code);
