-- Migration 023 (PostgreSQL): expand commercial invoice headers for Phenix-parity fields.

ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS payment_method TEXT NOT NULL DEFAULT 'Cash';
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS receipt_no TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS reference_no TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS branch_name TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS sales_rep_name TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS cost_center TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS party_account_name TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS goods_account_name TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS warehouse_name TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS extra_amount NUMERIC NOT NULL DEFAULT 0;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS party_code TEXT;

ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS payment_method TEXT NOT NULL DEFAULT 'Cash';
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS receipt_no TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS reference_no TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS branch_name TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS sales_rep_name TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS cost_center TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS party_account_name TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS goods_account_name TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS warehouse_name TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS extra_amount NUMERIC NOT NULL DEFAULT 0;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS party_code TEXT;

ALTER TABLE sales_document_lines ADD COLUMN IF NOT EXISTS warehouse_name TEXT;
ALTER TABLE purchase_document_lines ADD COLUMN IF NOT EXISTS warehouse_name TEXT;

ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS original_doc_id BIGINT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS original_doc_kind TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS original_doc_no TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS journal_doc_no TEXT;

ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS original_doc_id BIGINT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS original_doc_kind TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS original_doc_no TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS journal_doc_no TEXT;
