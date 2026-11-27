-- Migration 037 (SQLite): operations tables (ribbon completion RS-12).
-- pos_cash_closings: one row per POS terminal cash closing (expected vs
-- counted, difference computed by the service, never typed by hand).
-- support_tickets: local ticket register for Support / Suggestion windows;
-- tickets are persisted locally (no mail server is assumed).
-- Runtime services must not create tables.

CREATE TABLE IF NOT EXISTS pos_cash_closings (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    terminal_code TEXT NOT NULL,
    business_date TEXT NOT NULL,
    opened_by TEXT,
    closed_by TEXT,
    opening_amount REAL NOT NULL DEFAULT 0,
    expected_amount REAL NOT NULL DEFAULT 0,
    counted_amount REAL NOT NULL DEFAULT 0,
    difference REAL NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Closed',
    notes TEXT,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_pos_cash_closings_company_date
    ON pos_cash_closings(company_id, business_date, terminal_code);

CREATE TABLE IF NOT EXISTS support_tickets (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    ticket_no INTEGER NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('SUPPORT', 'SUGGESTION')),
    subject TEXT NOT NULL,
    body TEXT NOT NULL,
    contact_email TEXT,
    status TEXT NOT NULL DEFAULT 'Open',
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT,
    UNIQUE (company_id, ticket_no)
);

CREATE INDEX IF NOT EXISTS ix_support_tickets_company_kind
    ON support_tickets(company_id, kind, status);
