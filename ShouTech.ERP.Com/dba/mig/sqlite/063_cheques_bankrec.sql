-- ============================================================================
-- 063 - Cheques per-cheque GL link + real bank-statement model (SQLite)
--
-- Fix Phase Step 8 (sweep cheques-bankrec.md: CHQ-01..05, BNK-01/02,
-- BNKC-01):
--
-- 1. cheques gains batch_doc_no (the voucher number shown in ChqWin,
--    CHQ-05: it was displayed but never persisted) and cancel_reason
--    (the stop-payment note for outgoing cancellation). Per-cheque
--    journal entries themselves need no new column: journal_entry_id
--    already links a cheque to a journal entry -- from this step the
--    service posts ONE entry per cheque (source link, migration 060:
--    source_doc_type CHQ-ISSUE / CHQ-CLEAR / CHQ-BOUNCE / CHQ-ENDORSE /
--    CHQ-CANCEL / CHQ-FEE, source_doc_id = the cheque id), so the id
--    stored here is that cheque's OWN issue entry, never a batch entry
--    shared with siblings. Historical batch entries keep the shared id
--    they were posted with; the service detects the sharing at
--    bounce/cancel time and posts a per-cheque compensating entry
--    instead of reversing the shared entry (see FIX_PHASE_STEP8.md).
--
-- 2. The bank-statement side of reconciliation becomes DATA (BNK-01:
--    "Difference" was identically 0.00 because no statement existed):
--      bank_statements       one statement per bank account + period:
--                            opening balance, closing (statement)
--                            balance, status Open/Closed.
--      bank_statement_lines  the statement's own lines (entered in
--                            BankRecWin; file import is a separate
--                            flagged gap), each optionally matched to
--                            one book (journal) line.
--    Difference = closing_balance - opening_balance - SUM(matched book
--    lines) is then a REAL quantity: it is non-zero whenever the bank
--    statement disagrees with the books, moves by the matched amounts
--    as matching proceeds, and is legitimately 0 only when every
--    statement movement is matched in the books.
--    bank_reconciliation_marks gains the statement link so a mark
--    records WHICH statement line cleared the book line.
--
-- 3. Banks canonical store (BNKC-01): `banks` (migration 031) is the
--    ONE bank-account store -- BnkCard / MasterCardService /
--    BankRecWin already read and write it. The migration-050
--    `bank_accounts` table was written only by CardStore methods with
--    zero callers; any rows it somehow holds are copied into `banks`
--    here (code-keyed, only where the code is not already present),
--    the CardStore bank path and the DatWin "BankAccounts" dataset are
--    removed in code, and no code path writes bank_accounts any more.
--    The table itself is left in place as a legacy artifact (dropping
--    it would break STEI packages that still carry it); it is dead.
--
-- Idempotent: column adds are guarded by the migrator directives, the
-- tables/indexes are IF NOT EXISTS, and the banks copy only inserts
-- codes that are not already present; the checksum ledger also skips
-- a re-run.
-- ============================================================================

-- ---- Cheques: persisted batch voucher number + cancel reason ----------------
-- @if-column-missing cheques.batch_doc_no
ALTER TABLE cheques ADD COLUMN batch_doc_no TEXT;
-- @if-column-missing cheques.cancel_reason
ALTER TABLE cheques ADD COLUMN cancel_reason TEXT;

-- ---- Bank statements ---------------------------------------------------------
CREATE TABLE IF NOT EXISTS bank_statements (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    account_code TEXT NOT NULL,
    statement_date TEXT NOT NULL,
    statement_ref TEXT,
    period_from TEXT,
    period_to TEXT,
    opening_balance REAL NOT NULL DEFAULT 0,
    closing_balance REAL NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Open' CHECK (status IN ('Open','Closed')),
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
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
    amount REAL NOT NULL DEFAULT 0,
    matched_journal_line_key TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_bank_statement_lines_statement
    ON bank_statement_lines(statement_id, line_date);

CREATE INDEX IF NOT EXISTS ix_bank_statement_lines_matched
    ON bank_statement_lines(company_id, matched_journal_line_key);

-- ---- Marks learn which statement line cleared them ---------------------------
-- @if-column-missing bank_reconciliation_marks.statement_id
ALTER TABLE bank_reconciliation_marks ADD COLUMN statement_id TEXT;
-- @if-column-missing bank_reconciliation_marks.statement_line_id
ALTER TABLE bank_reconciliation_marks ADD COLUMN statement_line_id TEXT;

-- ---- Canonical banks: copy any legacy bank_accounts rows into banks ---------
INSERT INTO banks (id, company_id, code, name_en, branch_name, account_no, iban,
                   currency_code, gl_account_code, is_active)
SELECT lower(hex(randomblob(16))), ba.company_id, ba.bank_code, ba.bank_name,
       NULLIF(ba.branch, ''), NULLIF(ba.account_no, ''), NULLIF(ba.iban, ''),
       ba.currency_code, NULLIF(ba.gl_account_code, ''), ba.is_active
FROM bank_accounts ba
WHERE ba.is_deleted = 0
  AND NOT EXISTS (
      SELECT 1 FROM banks b
      WHERE b.company_id = ba.company_id AND b.code = ba.bank_code AND b.is_deleted = 0);
