-- ============================================================================
-- 060 - Document GL posting link (SQLite)
--
-- Fix Phase Step 3 (sweep defect J-2): operational documents (invoices,
-- POS sales, order conversions, manufacturing completions, asset
-- depreciation) now create REAL journal entries. The entry carries its
-- source document on the header so reports, reversal and audit can trace
-- GL -> document and document -> GL without parsing document_number:
--   source_doc_type  e.g. SAL / PUR / SRT / PRT / POS / ORD-INV / MFG / AST-DEP
--   source_doc_id    document id in its own store (text: the stores mix
--                    integer doc_id and Guid ids)
--   source_doc_no    human document number at posting time
-- document_number on the entry remains the journal's own number
-- (JV-<source doc no> for document postings); journal_doc_no on the
-- document header is stamped with that same real number.
-- Idempotent: column adds are guarded by the migrator directives and the
-- index is IF NOT EXISTS; the checksum ledger also skips a re-run.
-- ============================================================================

-- @if-column-missing journal_entries.source_doc_type
ALTER TABLE journal_entries ADD COLUMN source_doc_type TEXT NULL;
-- @if-column-missing journal_entries.source_doc_id
ALTER TABLE journal_entries ADD COLUMN source_doc_id TEXT NULL;
-- @if-column-missing journal_entries.source_doc_no
ALTER TABLE journal_entries ADD COLUMN source_doc_no TEXT NULL;

CREATE INDEX IF NOT EXISTS ix_journal_entries_source_doc
    ON journal_entries(company_id, source_doc_type, source_doc_id);
