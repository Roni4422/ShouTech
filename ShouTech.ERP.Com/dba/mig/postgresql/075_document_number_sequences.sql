-- Atomic, company-scoped document number allocation.
CREATE TABLE IF NOT EXISTS document_number_sequences (
    company_id TEXT NOT NULL,
    doc_kind TEXT NOT NULL,
    doc_year INTEGER NOT NULL,
    last_value BIGINT NOT NULL CHECK (last_value >= 0),
    PRIMARY KEY (company_id, doc_kind, doc_year)
);
