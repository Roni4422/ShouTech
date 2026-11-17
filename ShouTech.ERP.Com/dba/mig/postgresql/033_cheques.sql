-- PostgreSQL counterpart of sqlite/033_cheques.sql (Cheques portfolio, RS-4).
-- Money uses NUMERIC (never float) on PostgreSQL; ids are uuid per 026 style.

CREATE TABLE IF NOT EXISTS cheques (
    id uuid PRIMARY KEY,
    company_id TEXT NOT NULL,
    direction TEXT NOT NULL CHECK (direction IN ('IN','OUT')),
    cheque_no TEXT NOT NULL,
    bank_name TEXT NOT NULL,
    branch_name TEXT,
    drawer_name TEXT,
    party_account_code TEXT,
    party_name TEXT,
    amount NUMERIC NOT NULL CHECK (amount > 0),
    currency_code TEXT NOT NULL DEFAULT 'SAR',
    exchange_rate NUMERIC NOT NULL DEFAULT 1 CHECK (exchange_rate > 0),
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
    fees_amount NUMERIC NOT NULL DEFAULT 0 CHECK (fees_amount >= 0),
    journal_entry_id TEXT,
    notes TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_cheques_company_bank_drawer_no
    ON cheques(company_id, bank_name, drawer_name, cheque_no) WHERE is_deleted = FALSE;

CREATE INDEX IF NOT EXISTS ix_cheques_company_status_due
    ON cheques(company_id, status, due_date);
