-- Preserve the visible journal document number separately from the external reference.
ALTER TABLE journal_entries ADD COLUMN IF NOT EXISTS document_number varchar(100);
