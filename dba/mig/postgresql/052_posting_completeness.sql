-- PostgreSQL counterpart of sqlite/052_posting_completeness.sql (Phase A2):
-- journal_line_cost_splits (applied cost-center distribution of posted
-- journal lines) and journal_audit_events event_type 4 (draft updated).
-- Runtime services must not create tables.

CREATE TABLE IF NOT EXISTS journal_line_cost_splits (
    id uuid PRIMARY KEY,
    company_id TEXT NOT NULL,
    journal_entry_id uuid NOT NULL REFERENCES journal_entries(journal_entry_id),
    line_number INTEGER NOT NULL,
    cost_center_id uuid NOT NULL REFERENCES cost_centers(id),
    cost_center_code TEXT NOT NULL,
    percentage REAL NOT NULL,
    debit_amount NUMERIC NOT NULL DEFAULT 0,
    credit_amount NUMERIC NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_journal_line_cost_splits_entry
    ON journal_line_cost_splits(journal_entry_id, line_number);

CREATE INDEX IF NOT EXISTS ix_journal_line_cost_splits_center
    ON journal_line_cost_splits(company_id, cost_center_id);

ALTER TABLE journal_audit_events DROP CONSTRAINT IF EXISTS journal_audit_events_event_type_check;

ALTER TABLE journal_audit_events
    ADD CONSTRAINT journal_audit_events_event_type_check CHECK (event_type IN (1, 2, 3, 4));
