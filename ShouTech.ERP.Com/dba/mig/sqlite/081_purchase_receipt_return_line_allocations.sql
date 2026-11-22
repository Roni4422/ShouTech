-- Tracks quantities returned to suppliers before a supplier invoice exists.
-- One allocation ties each posted return line to the exact original receipt line.
CREATE TABLE IF NOT EXISTS purchase_receipt_return_line_allocations (
    allocation_id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    return_doc_id INTEGER NOT NULL,
    return_line_id INTEGER NOT NULL,
    original_doc_id INTEGER NOT NULL,
    original_line_id INTEGER NOT NULL,
    material_id INTEGER NOT NULL,
    returned_qty NUMERIC NOT NULL CHECK (returned_qty > 0),
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (company_id, return_doc_id, return_line_id)
);

CREATE INDEX IF NOT EXISTS ix_purchase_receipt_return_allocations_receipt_line
    ON purchase_receipt_return_line_allocations(company_id, original_doc_id, original_line_id);
CREATE INDEX IF NOT EXISTS ix_purchase_receipt_return_allocations_return_doc
    ON purchase_receipt_return_line_allocations(company_id, return_doc_id);
