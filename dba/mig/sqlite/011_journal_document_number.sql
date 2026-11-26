-- Preserve the visible journal document number separately from the external reference.
ALTER TABLE journal_entries ADD COLUMN document_number TEXT NULL;
