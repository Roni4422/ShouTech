-- Posting completeness (Phase A2):
-- 1) journal_line_cost_splits: the applied cost-center distribution of every
--    posted journal line, written by JournalRepository inside the posting
--    transaction. One row per (line, target cost center): when a line's cost
--    center has an active allocation (migration 031), the line amount is
--    split across the allocation targets by percentage; otherwise the line
--    maps 100% to its own leaf cost center. The SQLite table declares no
--    FKs (the party_ledger convention on this provider); the PostgreSQL
--    counterpart declares uuid FKs. The integrity of the rows themselves
--    is enforced by the posting transaction that writes them.
-- 2) journal_audit_events: allow event_type 4 (draft updated) alongside
--    1 (created), 2 (posted), 3 (reversed). SQLite cannot alter a CHECK
--    constraint in place, so the table is rebuilt preserving every row.
-- Runtime services must not create tables.

CREATE TABLE IF NOT EXISTS journal_line_cost_splits (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    journal_entry_id TEXT NOT NULL,
    line_number INTEGER NOT NULL,
    cost_center_id TEXT NOT NULL,
    cost_center_code TEXT NOT NULL,
    percentage REAL NOT NULL,
    debit_amount NUMERIC NOT NULL DEFAULT 0,
    credit_amount NUMERIC NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS ix_journal_line_cost_splits_entry
    ON journal_line_cost_splits(journal_entry_id, line_number);

CREATE INDEX IF NOT EXISTS ix_journal_line_cost_splits_center
    ON journal_line_cost_splits(company_id, cost_center_id);

CREATE TABLE journal_audit_events_new (
    journal_audit_event_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    journal_entry_id TEXT NOT NULL,
    event_type INTEGER NOT NULL CHECK (event_type IN (1, 2, 3, 4)),
    actor TEXT NOT NULL,
    event_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    details TEXT,
    FOREIGN KEY(journal_entry_id) REFERENCES journal_entries(journal_entry_id)
);

INSERT INTO journal_audit_events_new
    (journal_audit_event_id, journal_entry_id, event_type, actor, event_at, details)
SELECT journal_audit_event_id, journal_entry_id, event_type, actor, event_at, details
FROM journal_audit_events;

DROP TABLE journal_audit_events;

ALTER TABLE journal_audit_events_new RENAME TO journal_audit_events;

CREATE INDEX IF NOT EXISTS ix_journal_audit_events_entry_time
    ON journal_audit_events(journal_entry_id, event_at, journal_audit_event_id);
