-- Owns cross-document chain links (Quotation -> Sales Order -> Delivery Note -> Invoice,
-- and RFQ -> Purchase Order -> Receipt -> Purchase Invoice).
-- A link records that the target document was created from the source document.
-- Runtime services must not create tables.

CREATE TABLE IF NOT EXISTS document_links (
    link_id INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    source_doc_kind TEXT NOT NULL,
    source_doc_id INTEGER NOT NULL,
    target_doc_kind TEXT NOT NULL,
    target_doc_id INTEGER NOT NULL,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (company_id, source_doc_kind, source_doc_id, target_doc_kind, target_doc_id)
);

CREATE INDEX IF NOT EXISTS ix_document_links_source
    ON document_links(company_id, source_doc_kind, source_doc_id);

CREATE INDEX IF NOT EXISTS ix_document_links_target
    ON document_links(company_id, target_doc_kind, target_doc_id);
