-- Migration 035 (PostgreSQL): bank reconciliation marks (ribbon completion RS-9).
-- Counterpart of sqlite/035_bank_reconciliation.sql.
--
-- journal_line_key choice: journal_entry_lines HAS a stable surrogate key,
-- journal_entry_line_id (bigint identity on PostgreSQL, INTEGER PRIMARY KEY
-- AUTOINCREMENT on SQLite), unique per line. The mark stores
-- CAST(journal_entry_line_id AS TEXT) as journal_line_key -- no composite
-- fallback is needed.

CREATE TABLE IF NOT EXISTS bank_reconciliation_marks (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    journal_entry_id TEXT NOT NULL,
    journal_line_key TEXT NOT NULL,
    account_code TEXT NOT NULL,
    statement_date TEXT NOT NULL,
    statement_ref TEXT,
    cleared_amount DOUBLE PRECISION NOT NULL DEFAULT 0,
    is_cleared INTEGER NOT NULL DEFAULT 1 CHECK (is_cleared IN (0, 1)),
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ,
    UNIQUE (company_id, journal_line_key)
);

CREATE INDEX IF NOT EXISTS ix_bank_rec_marks_account
    ON bank_reconciliation_marks(company_id, account_code, statement_date);
