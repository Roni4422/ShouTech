-- Preserve allocation provenance when a linked receipt/payment voucher is reversed.
CREATE TABLE IF NOT EXISTS payment_allocation_reversal_audit (
    audit_id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    allocation_id TEXT NOT NULL,
    payment_ledger_id INTEGER NOT NULL,
    doc_table TEXT NOT NULL CHECK (doc_table IN ('SALES', 'PURCHASE')),
    doc_id INTEGER NOT NULL,
    document_company_id TEXT NOT NULL,
    amount NUMERIC NOT NULL CHECK (amount > 0),
    allocated_by TEXT,
    note TEXT,
    released_by TEXT NOT NULL,
    reversal_journal_id TEXT NOT NULL,
    released_at TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (company_id, allocation_id)
);

CREATE INDEX IF NOT EXISTS ix_payment_allocation_reversal_audit_payment
    ON payment_allocation_reversal_audit(company_id, payment_ledger_id);
CREATE INDEX IF NOT EXISTS ix_payment_allocation_reversal_audit_document
    ON payment_allocation_reversal_audit(company_id, doc_table, doc_id);
