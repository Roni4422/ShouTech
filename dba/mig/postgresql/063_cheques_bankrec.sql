-- ============================================================================
-- 063 - Cheques per-cheque GL link + real bank-statement model (PostgreSQL)
-- PostgreSQL counterpart of sqlite/063_cheques_bankrec.sql (Fix Phase
-- Step 8, sweep cheques-bankrec.md). See the SQLite file for the full
-- design notes: cheques.batch_doc_no / cancel_reason, the
-- bank_statements + bank_statement_lines statement side that makes
-- BankRecWin's Difference a real quantity, marks linked to statement
-- lines, and the one-time copy of legacy bank_accounts rows into the
-- canonical `banks` store (BNKC-01).
-- Idempotent: ADD COLUMN IF NOT EXISTS, CREATE ... IF NOT EXISTS, and
-- the banks copy only inserts codes not already present.
-- ============================================================================

ALTER TABLE cheques ADD COLUMN IF NOT EXISTS batch_doc_no TEXT;
ALTER TABLE cheques ADD COLUMN IF NOT EXISTS cancel_reason TEXT;

CREATE TABLE IF NOT EXISTS bank_statements (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    account_code TEXT NOT NULL,
    statement_date TEXT NOT NULL,
    statement_ref TEXT,
    period_from TEXT,
    period_to TEXT,
    opening_balance NUMERIC NOT NULL DEFAULT 0,
    closing_balance NUMERIC NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Open' CHECK (status IN ('Open','Closed')),
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS ix_bank_statements_company_account
    ON bank_statements(company_id, account_code, statement_date);

CREATE TABLE IF NOT EXISTS bank_statement_lines (
    id TEXT PRIMARY KEY,
    statement_id TEXT NOT NULL,
    company_id TEXT NOT NULL,
    account_code TEXT NOT NULL,
    line_date TEXT NOT NULL,
    reference TEXT,
    description TEXT,
    amount NUMERIC NOT NULL DEFAULT 0,
    matched_journal_line_key TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS ix_bank_statement_lines_statement
    ON bank_statement_lines(statement_id, line_date);

CREATE INDEX IF NOT EXISTS ix_bank_statement_lines_matched
    ON bank_statement_lines(company_id, matched_journal_line_key);

ALTER TABLE bank_reconciliation_marks ADD COLUMN IF NOT EXISTS statement_id TEXT;
ALTER TABLE bank_reconciliation_marks ADD COLUMN IF NOT EXISTS statement_line_id TEXT;

-- Canonical banks: copy any legacy bank_accounts rows into banks.
-- banks.id is uuid on PostgreSQL: derive a stable uuid from the legacy
-- row identity (md5 hex with hyphens inserted).
INSERT INTO banks (id, company_id, code, name_en, branch_name, account_no, iban,
                   currency_code, gl_account_code, is_active)
SELECT (substr(m, 1, 8) || '-' || substr(m, 9, 4) || '-' || substr(m, 13, 4) || '-' ||
        substr(m, 17, 4) || '-' || substr(m, 21, 12))::uuid,
       ba.company_id, ba.bank_code, ba.bank_name,
       NULLIF(ba.branch, ''), NULLIF(ba.account_no, ''), NULLIF(ba.iban, ''),
       ba.currency_code, NULLIF(ba.gl_account_code, ''),
       (ba.is_active <> 0)
FROM bank_accounts ba,
     LATERAL (SELECT md5(ba.company_id || ':' || ba.bank_id::text || ':' || ba.bank_code) AS m) x
WHERE ba.is_deleted = 0
  AND NOT EXISTS (
      SELECT 1 FROM banks b
      WHERE b.company_id = ba.company_id AND b.code = ba.bank_code AND b.is_deleted = false);
