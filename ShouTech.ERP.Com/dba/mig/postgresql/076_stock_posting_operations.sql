-- Durable idempotency receipts for dedicated stock posting engines.
-- The receipt is committed in the same transaction as all stock movements.
CREATE TABLE IF NOT EXISTS stock_posting_operations (
    company_id TEXT NOT NULL,
    operation_key TEXT NOT NULL,
    operation_type TEXT NOT NULL,
    document_no TEXT NOT NULL,
    movement_count INTEGER NOT NULL DEFAULT 0 CHECK (movement_count >= 0),
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (company_id, operation_key)
);

ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS posting_operation_key TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS posting_operation_key TEXT;
