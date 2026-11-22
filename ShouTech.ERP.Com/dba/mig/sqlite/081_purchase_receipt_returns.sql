-- Line-level lineage for goods returned to a supplier before supplier invoicing.
-- Allocations are inserted in the same transaction as stock and GRNI reversal.
CREATE TABLE IF NOT EXISTS purchase_receipt_return_allocations (
    allocation_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    return_doc_id INTEGER NOT NULL,
    return_line_id INTEGER NOT NULL,
    original_doc_id INTEGER NOT NULL,
    original_line_id INTEGER NOT NULL,
    material_id INTEGER NOT NULL,
    returned_qty NUMERIC NOT NULL CHECK (returned_qty > 0),
    created_by TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    CONSTRAINT uq_purchase_receipt_return_line
        UNIQUE (company_id, return_doc_id, return_line_id)
);
CREATE INDEX IF NOT EXISTS ix_purchase_receipt_return_source_line
    ON purchase_receipt_return_allocations (company_id, original_doc_id, original_line_id);
