-- PostgreSQL counterpart of sqlite/025_payment_allocations.sql.
-- Owns payment-to-document allocations for the Payment Reconciliation window.

CREATE TABLE IF NOT EXISTS payment_allocations (
    allocation_id uuid PRIMARY KEY,
    company_id TEXT NOT NULL,
    party_kind TEXT NOT NULL CHECK (party_kind IN ('C','S')),
    party_id BIGINT NOT NULL,
    payment_ledger_id BIGINT NOT NULL,
    doc_table TEXT NOT NULL CHECK (doc_table IN ('SALES','PURCHASE')),
    doc_id BIGINT NOT NULL,
    doc_no TEXT,
    amount NUMERIC NOT NULL CHECK (amount > 0),
    allocated_by TEXT,
    allocated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    note TEXT
);

CREATE INDEX IF NOT EXISTS ix_payment_allocations_payment
    ON payment_allocations(company_id, payment_ledger_id);

CREATE INDEX IF NOT EXISTS ix_payment_allocations_doc
    ON payment_allocations(company_id, doc_table, doc_id);
