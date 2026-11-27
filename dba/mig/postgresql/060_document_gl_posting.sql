-- ============================================================================
-- 060 - Document GL posting link (PostgreSQL)
-- PostgreSQL counterpart of sqlite/060_document_gl_posting.sql (Fix Phase
-- Step 3, sweep defect J-2). See the SQLite file for the design notes.
-- ============================================================================

ALTER TABLE journal_entries ADD COLUMN IF NOT EXISTS source_doc_type TEXT NULL;
ALTER TABLE journal_entries ADD COLUMN IF NOT EXISTS source_doc_id TEXT NULL;
ALTER TABLE journal_entries ADD COLUMN IF NOT EXISTS source_doc_no TEXT NULL;

CREATE INDEX IF NOT EXISTS ix_journal_entries_source_doc
    ON journal_entries(company_id, source_doc_type, source_doc_id);
