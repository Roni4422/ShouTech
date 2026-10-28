-- Migration 023 (SQLite): expand commercial invoice headers for Phenix-parity fields.
-- Adds payment method, accounts, cost centre, sales rep, reference fields,
-- extra amount, and ensures paid/discount are usable on save.

-- sales_documents
ALTER TABLE sales_documents ADD COLUMN payment_method TEXT NOT NULL DEFAULT 'Cash';
ALTER TABLE sales_documents ADD COLUMN receipt_no TEXT;
ALTER TABLE sales_documents ADD COLUMN reference_no TEXT;
ALTER TABLE sales_documents ADD COLUMN branch_name TEXT;
ALTER TABLE sales_documents ADD COLUMN sales_rep_name TEXT;
ALTER TABLE sales_documents ADD COLUMN cost_center TEXT;
ALTER TABLE sales_documents ADD COLUMN party_account_name TEXT;
ALTER TABLE sales_documents ADD COLUMN goods_account_name TEXT;
ALTER TABLE sales_documents ADD COLUMN warehouse_name TEXT;
ALTER TABLE sales_documents ADD COLUMN extra_amount NUMERIC NOT NULL DEFAULT 0;
ALTER TABLE sales_documents ADD COLUMN party_code TEXT;

-- purchase_documents
ALTER TABLE purchase_documents ADD COLUMN payment_method TEXT NOT NULL DEFAULT 'Cash';
ALTER TABLE purchase_documents ADD COLUMN receipt_no TEXT;
ALTER TABLE purchase_documents ADD COLUMN reference_no TEXT;
ALTER TABLE purchase_documents ADD COLUMN branch_name TEXT;
ALTER TABLE purchase_documents ADD COLUMN sales_rep_name TEXT;
ALTER TABLE purchase_documents ADD COLUMN cost_center TEXT;
ALTER TABLE purchase_documents ADD COLUMN party_account_name TEXT;
ALTER TABLE purchase_documents ADD COLUMN goods_account_name TEXT;
ALTER TABLE purchase_documents ADD COLUMN warehouse_name TEXT;
ALTER TABLE purchase_documents ADD COLUMN extra_amount NUMERIC NOT NULL DEFAULT 0;
ALTER TABLE purchase_documents ADD COLUMN party_code TEXT;

-- line warehouse name (optional display; id remains authoritative)
ALTER TABLE sales_document_lines ADD COLUMN warehouse_name TEXT;
ALTER TABLE purchase_document_lines ADD COLUMN warehouse_name TEXT;

-- Original invoice link (Phenix "الأصل") and posted journal voucher number (Phenix "السند")
ALTER TABLE sales_documents ADD COLUMN original_doc_id INTEGER;
ALTER TABLE sales_documents ADD COLUMN original_doc_kind TEXT;
ALTER TABLE sales_documents ADD COLUMN original_doc_no TEXT;
ALTER TABLE sales_documents ADD COLUMN journal_doc_no TEXT;

ALTER TABLE purchase_documents ADD COLUMN original_doc_id INTEGER;
ALTER TABLE purchase_documents ADD COLUMN original_doc_kind TEXT;
ALTER TABLE purchase_documents ADD COLUMN original_doc_no TEXT;
ALTER TABLE purchase_documents ADD COLUMN journal_doc_no TEXT;
