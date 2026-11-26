-- Line-level three-way match between purchase receipts and supplier invoices.
-- The match quantity is the authoritative quantity consumed from each receipt
-- line; cost and FX snapshots make GRNI and price variance auditable.
CREATE TABLE IF NOT EXISTS purchase_receipt_invoice_matches (
    match_id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    purchase_order_id INTEGER NOT NULL,
    receipt_doc_id INTEGER NOT NULL,
    receipt_line_id INTEGER NOT NULL,
    invoice_doc_id INTEGER NOT NULL,
    invoice_line_id INTEGER NOT NULL,
    material_id INTEGER NOT NULL,
    matched_qty NUMERIC NOT NULL CHECK (matched_qty > 0),
    receipt_unit_cost NUMERIC NOT NULL CHECK (receipt_unit_cost >= 0),
    receipt_exchange_rate NUMERIC NOT NULL CHECK (receipt_exchange_rate > 0),
    invoice_unit_cost NUMERIC NOT NULL CHECK (invoice_unit_cost >= 0),
    invoice_exchange_rate NUMERIC NOT NULL CHECK (invoice_exchange_rate > 0),
    receipt_base_amount NUMERIC NOT NULL,
    invoice_base_amount NUMERIC NOT NULL,
    variance_base_amount NUMERIC NOT NULL,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (company_id, receipt_line_id, invoice_line_id)
);

CREATE INDEX IF NOT EXISTS ix_purchase_receipt_matches_receipt
    ON purchase_receipt_invoice_matches(company_id, receipt_doc_id, receipt_line_id);
CREATE INDEX IF NOT EXISTS ix_purchase_receipt_matches_invoice
    ON purchase_receipt_invoice_matches(company_id, invoice_doc_id);
CREATE INDEX IF NOT EXISTS ix_purchase_receipt_matches_order
    ON purchase_receipt_invoice_matches(company_id, purchase_order_id, material_id);
