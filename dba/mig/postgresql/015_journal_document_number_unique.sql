-- Prevent duplicate visible journal numbers within a company while permitting legacy nulls.
CREATE UNIQUE INDEX IF NOT EXISTS ux_journal_entries_company_document_number
    ON journal_entries(company_id, document_number)
    WHERE document_number IS NOT NULL AND length(trim(document_number)) > 0;
