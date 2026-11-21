-- PostgreSQL counterpart of sqlite/079_payment_allocation_reversal_audit.sql.
CREATE TABLE IF NOT EXISTS payment_allocation_reversal_audit (
    audit_id UUID PRIMARY KEY,
    company_id TEXT NOT NULL,
    allocation_id UUID NOT NULL,
    payment_ledger_id BIGINT NOT NULL,
    doc_table TEXT NOT NULL CHECK (doc_table IN ('SALES', 'PURCHASE')),
    doc_id BIGINT NOT NULL,
    document_company_id TEXT NOT NULL,
    amount NUMERIC NOT NULL CHECK (amount > 0),
    allocated_by TEXT,
    note TEXT,
    released_by TEXT NOT NULL,
    reversal_journal_id UUID NOT NULL,
    released_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (company_id, allocation_id)
);

CREATE INDEX IF NOT EXISTS ix_payment_allocation_reversal_audit_payment
    ON payment_allocation_reversal_audit(company_id, payment_ledger_id);
CREATE INDEX IF NOT EXISTS ix_payment_allocation_reversal_audit_document
    ON payment_allocation_reversal_audit(company_id, doc_table, doc_id);
