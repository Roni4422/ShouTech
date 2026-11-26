-- Keep the party-ledger company key separate from the operational document key.
-- Legacy document writers use the company code; the commercial order repository
-- uses the company's stable GUID. Reconciliation must retain both scopes.
-- @if-column-missing payment_allocations.document_company_id
ALTER TABLE payment_allocations ADD COLUMN document_company_id TEXT;

UPDATE payment_allocations
SET document_company_id = CASE
    WHEN doc_table = 'SALES' THEN
        (SELECT d.company_id FROM sales_documents d WHERE d.doc_id = payment_allocations.doc_id)
    WHEN doc_table = 'PURCHASE' THEN
        (SELECT d.company_id FROM purchase_documents d WHERE d.doc_id = payment_allocations.doc_id)
    ELSE NULL
END
WHERE document_company_id IS NULL;

CREATE INDEX IF NOT EXISTS ix_payment_allocations_document_company
    ON payment_allocations(document_company_id, doc_table, doc_id);
