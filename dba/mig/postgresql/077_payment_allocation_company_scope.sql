-- PostgreSQL counterpart of sqlite/077_payment_allocation_company_scope.sql.
-- Preserve the company-code key for party_ledger and the document's own key.
ALTER TABLE payment_allocations
    ADD COLUMN IF NOT EXISTS document_company_id TEXT;

UPDATE payment_allocations
SET document_company_id = CASE
    WHEN doc_table = 'SALES' THEN
        (SELECT d.company_id::text FROM sales_documents d WHERE d.doc_id = payment_allocations.doc_id)
    WHEN doc_table = 'PURCHASE' THEN
        (SELECT d.company_id::text FROM purchase_documents d WHERE d.doc_id = payment_allocations.doc_id)
    ELSE NULL
END
WHERE document_company_id IS NULL;

CREATE INDEX IF NOT EXISTS ix_payment_allocations_document_company
    ON payment_allocations(document_company_id, doc_table, doc_id);
