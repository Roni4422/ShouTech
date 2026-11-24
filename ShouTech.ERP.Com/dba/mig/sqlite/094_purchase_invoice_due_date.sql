-- 094 - Purchase invoice due date (SQLite).
-- Y3 (payable aging by due date): purchase_documents gains the due date the
-- Accounts Payable aging report buckets open invoices by. NULL on rows that
-- predate this migration; readers fall back to doc_date, and new invoices
-- are stamped at creation (doc date + supplier credit_days unless the user
-- picked an explicit due date in InvoiceWin).
-- @if-column-missing purchase_documents.due_date
ALTER TABLE purchase_documents ADD COLUMN due_date TEXT;
-- @endif

CREATE INDEX IF NOT EXISTS ix_purchase_documents_due_date
    ON purchase_documents (company_id, doc_kind, status, due_date);
