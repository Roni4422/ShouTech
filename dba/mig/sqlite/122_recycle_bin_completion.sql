-- 122 - Recycle-bin completion (SOFT_DELETE_STUDY): bin metadata on the
-- document tables, soft-delete columns on journal_entries, and the
-- bin_posting_events exactly-once ledger. Additive only: every existing
-- consumer keys on is_deleted as before; deleted_at/deleted_by are
-- display/audit metadata for the bin, and bin_posting_events is written
-- only by DocBinSvc.

-- @if-column-missing sales_documents.deleted_at
ALTER TABLE sales_documents ADD COLUMN deleted_at TEXT;
-- @if-column-missing sales_documents.deleted_by
ALTER TABLE sales_documents ADD COLUMN deleted_by TEXT;
-- @if-column-missing purchase_documents.deleted_at
ALTER TABLE purchase_documents ADD COLUMN deleted_at TEXT;
-- @if-column-missing purchase_documents.deleted_by
ALTER TABLE purchase_documents ADD COLUMN deleted_by TEXT;
-- @if-column-missing parties.deleted_at
ALTER TABLE parties ADD COLUMN deleted_at TEXT;
-- @if-column-missing parties.deleted_by
ALTER TABLE parties ADD COLUMN deleted_by TEXT;
-- @if-column-missing units_of_measure.deleted_at
ALTER TABLE units_of_measure ADD COLUMN deleted_at TEXT;
-- @if-column-missing units_of_measure.deleted_by
ALTER TABLE units_of_measure ADD COLUMN deleted_by TEXT;
-- @if-column-missing cheques.deleted_at
ALTER TABLE cheques ADD COLUMN deleted_at TEXT;
-- @if-column-missing cheques.deleted_by
ALTER TABLE cheques ADD COLUMN deleted_by TEXT;
-- @if-column-missing pos_sessions.deleted_at
ALTER TABLE pos_sessions ADD COLUMN deleted_at TEXT;
-- @if-column-missing pos_sessions.deleted_by
ALTER TABLE pos_sessions ADD COLUMN deleted_by TEXT;

-- @if-column-missing journal_entries.is_deleted
ALTER TABLE journal_entries ADD COLUMN is_deleted INTEGER NOT NULL DEFAULT 0;
-- @if-column-missing journal_entries.deleted_at
ALTER TABLE journal_entries ADD COLUMN deleted_at TEXT;
-- @if-column-missing journal_entries.deleted_by
ALTER TABLE journal_entries ADD COLUMN deleted_by TEXT;

-- Exactly-once ledger for bin posting compensation: a NEUTRALIZE event
-- with no later REINSTATE event means the subject is currently binned
-- with neutralized posting effects. DocBinSvc checks and writes these
-- rows inside the same transaction as the effects themselves.
CREATE TABLE IF NOT EXISTS bin_posting_events (
    event_id TEXT PRIMARY KEY,
    company_key TEXT NOT NULL,
    subject_kind TEXT NOT NULL,
    doc_table TEXT NOT NULL,
    doc_kind TEXT NOT NULL,
    doc_key TEXT NOT NULL,
    action TEXT NOT NULL CHECK (action IN ('NEUTRALIZE', 'REINSTATE')),
    journal_entry_id TEXT,
    actor TEXT NOT NULL,
    created_at TEXT NOT NULL,
    note TEXT
);
CREATE INDEX IF NOT EXISTS ix_bin_posting_events_subject
    ON bin_posting_events(company_key, subject_kind, doc_table, doc_key, created_at);
