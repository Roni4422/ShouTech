-- 072 - Use the source document date for tax-period reporting.
-- @if-column-missing tax_transactions.document_date
ALTER TABLE tax_transactions ADD COLUMN document_date TEXT;
UPDATE tax_transactions
SET document_date = substr(posted_at, 1, 10)
WHERE document_date IS NULL OR document_date = '';
CREATE INDEX IF NOT EXISTS ix_tax_transactions_document_period
    ON tax_transactions(company_code, country_code, document_date, tax_direction, tax_code);
