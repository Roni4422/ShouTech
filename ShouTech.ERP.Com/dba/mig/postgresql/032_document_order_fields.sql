-- Migration 032 (PostgreSQL): order/quotation scheduling fields for the
-- commercial document engine (ribbon completion, RS-5/RS-6).
-- delivery_date: promised delivery (sales) / required-by (purchase) date.
-- valid_till: quotation validity deadline (ERPNext Quotation pattern);
-- conversion of an expired quotation is blocked in the service layer.
-- Runtime services must not create tables.

ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS delivery_date TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS valid_till TEXT;

ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS delivery_date TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS valid_till TEXT;

CREATE INDEX IF NOT EXISTS ix_sales_documents_kind_status
    ON sales_documents (company_id, doc_kind, status, doc_date);

CREATE INDEX IF NOT EXISTS ix_purchase_documents_kind_status
    ON purchase_documents (company_id, doc_kind, status, doc_date);
