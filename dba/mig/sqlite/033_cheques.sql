-- Owns the Cheques portfolio (RS-4). A received cheque is not money until it
-- clears: Save posts Dr Portfolio/Box Cr Party (incoming) or Dr Party Cr Box
-- (outgoing) through the journal engine and stores journal_entry_id here.
-- Dates are stored as ISO text (yyyy-MM-dd) on both providers.
-- Runtime services must not create tables.

CREATE TABLE IF NOT EXISTS cheques (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    direction TEXT NOT NULL CHECK (direction IN ('IN','OUT')),
    cheque_no TEXT NOT NULL,
    bank_name TEXT NOT NULL,
    branch_name TEXT,
    drawer_name TEXT,
    party_account_code TEXT,
    party_name TEXT,
    amount REAL NOT NULL CHECK (amount > 0),
    currency_code TEXT NOT NULL DEFAULT 'SAR',
    exchange_rate REAL NOT NULL DEFAULT 1 CHECK (exchange_rate > 0),
    issue_date TEXT,
    due_date TEXT,
    status TEXT NOT NULL DEFAULT 'InPortfolio'
        CHECK (status IN ('InPortfolio','Issued','Deposited','Cleared','Bounced','Endorsed','Cancelled')),
    portfolio_account_code TEXT,
    deposit_bank_account_code TEXT,
    deposit_date TEXT,
    clearance_date TEXT,
    endorsed_to_account_code TEXT,
    bounce_date TEXT,
    bounce_reason TEXT,
    fees_amount REAL NOT NULL DEFAULT 0 CHECK (fees_amount >= 0),
    journal_entry_id TEXT,
    notes TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_cheques_company_bank_drawer_no
    ON cheques(company_id, bank_name, drawer_name, cheque_no) WHERE is_deleted = 0;

CREATE INDEX IF NOT EXISTS ix_cheques_company_status_due
    ON cheques(company_id, status, due_date);
