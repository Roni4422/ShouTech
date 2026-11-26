-- 069 - Immutable tax transaction register for posted commercial documents.
CREATE TABLE IF NOT EXISTS tax_transactions (
    tax_transaction_id INTEGER PRIMARY KEY AUTOINCREMENT,
    company_code TEXT NOT NULL,
    country_code TEXT NOT NULL,
    source_doc_kind TEXT NOT NULL,
    source_doc_id INTEGER NOT NULL,
    source_doc_no TEXT NOT NULL,
    line_no INTEGER NOT NULL,
    tax_code TEXT NOT NULL,
    tax_type TEXT NOT NULL,
    tax_treatment TEXT NOT NULL,
    tax_rate NUMERIC NOT NULL,
    taxable_amount NUMERIC NOT NULL,
    tax_amount NUMERIC NOT NULL,
    is_recoverable INTEGER NOT NULL CHECK (is_recoverable IN (0, 1)),
    tax_direction TEXT NOT NULL CHECK (tax_direction IN ('OUTPUT', 'INPUT')),
    tax_account_code TEXT NOT NULL DEFAULT '',
    currency_code TEXT NOT NULL,
    exchange_rate NUMERIC NOT NULL,
    base_tax_amount NUMERIC NOT NULL,
    journal_doc_no TEXT NOT NULL DEFAULT '',
    posted_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_by TEXT NOT NULL DEFAULT '',
    UNIQUE (company_code, source_doc_kind, source_doc_id, line_no, tax_code)
);
CREATE INDEX IF NOT EXISTS ix_tax_transactions_period
    ON tax_transactions(company_code, country_code, posted_at, tax_direction, tax_code);
CREATE INDEX IF NOT EXISTS ix_tax_transactions_source
    ON tax_transactions(company_code, source_doc_kind, source_doc_id);
