-- Purchase return lineage: each posted return line consumes quantity from one original invoice line.
-- Kept separate from document_links because quantity allocation is line-level and cumulative.
CREATE TABLE IF NOT EXISTS purchase_return_line_allocations (
    allocation_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    return_doc_id INTEGER NOT NULL,
    return_line_id INTEGER NOT NULL,
    original_doc_id INTEGER NOT NULL,
    original_line_id INTEGER NOT NULL,
    material_id INTEGER NOT NULL,
    returned_qty NUMERIC NOT NULL CHECK (returned_qty > 0),
    created_by TEXT NOT NULL,
    created_at TEXT NOT NULL,
    CONSTRAINT uq_purchase_return_line_allocation
        UNIQUE (company_id, return_doc_id, return_line_id)
);

CREATE INDEX IF NOT EXISTS ix_purchase_return_source_line
    ON purchase_return_line_allocations
       (company_id, original_doc_id, original_line_id);

CREATE INDEX IF NOT EXISTS ix_purchase_return_document
    ON purchase_return_line_allocations
       (company_id, return_doc_id);
