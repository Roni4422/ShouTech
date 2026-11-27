-- Owns payment-to-document allocations for the Payment Reconciliation window.
-- An allocation records that part of a party_ledger payment entry settles part
-- of an open sales/purchase document. Amounts are validated again inside the
-- allocation transaction (open amount and unallocated payment amount are
-- re-read under the transaction), so a double submission cannot over-allocate.
-- Runtime services must not create tables.

CREATE TABLE IF NOT EXISTS payment_allocations (
    allocation_id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    party_kind TEXT NOT NULL CHECK (party_kind IN ('C','S')),
    party_id INTEGER NOT NULL,
    payment_ledger_id INTEGER NOT NULL,
    doc_table TEXT NOT NULL CHECK (doc_table IN ('SALES','PURCHASE')),
    doc_id INTEGER NOT NULL,
    doc_no TEXT,
    amount NUMERIC NOT NULL CHECK (amount > 0),
    allocated_by TEXT,
    allocated_at TEXT NOT NULL DEFAULT (datetime('now')),
    note TEXT
);

CREATE INDEX IF NOT EXISTS ix_payment_allocations_payment
    ON payment_allocations(company_id, payment_ledger_id);

CREATE INDEX IF NOT EXISTS ix_payment_allocations_doc
    ON payment_allocations(company_id, doc_table, doc_id);
