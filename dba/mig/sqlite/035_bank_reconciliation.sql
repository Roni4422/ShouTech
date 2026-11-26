-- Migration 035 (SQLite): bank reconciliation marks (ribbon completion RS-9).
--
-- journal_line_key choice: journal_entry_lines HAS a stable surrogate key,
-- journal_entry_line_id (INTEGER PRIMARY KEY AUTOINCREMENT on SQLite,
-- bigint identity on PostgreSQL), unique per line and never reused within a
-- company database. The mark therefore stores CAST(journal_entry_line_id AS
-- TEXT) as journal_line_key -- no composite fallback is needed. The entry id
-- and account code are denormalized onto the mark for reporting and for
-- resilience if a line is ever renumbered.
--
-- A mark means: this journal line appeared on a bank statement (cleared).
-- Unmarking deletes the row. Runtime services must not create tables.

CREATE TABLE IF NOT EXISTS bank_reconciliation_marks (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    journal_entry_id TEXT NOT NULL,
    journal_line_key TEXT NOT NULL,
    account_code TEXT NOT NULL,
    statement_date TEXT NOT NULL,
    statement_ref TEXT,
    cleared_amount REAL NOT NULL DEFAULT 0,
    is_cleared INTEGER NOT NULL DEFAULT 1 CHECK (is_cleared IN (0, 1)),
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT,
    UNIQUE (company_id, journal_line_key)
);

CREATE INDEX IF NOT EXISTS ix_bank_rec_marks_account
    ON bank_reconciliation_marks(company_id, account_code, statement_date);
