-- 096 - Purchase-cycle completion B1 core (PostgreSQL): P3 PO header
-- facts, P4 stored line UOM factor + per-line required-by, P5 stored
-- per_received / per_billed, Q2 rejected qty at receipt, Q3 receipt
-- delivery-note number + bill identity (I1 bill_date), I5 PRT own
-- credit-note reference, P6 amended_from link, Q4 receipt
-- over-tolerance on the purchase match policy.
-- Companion to docs/PURCHASE_CYCLE_COMPLETION.md section 2 (P/Q/I).

ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS payment_terms TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS incoterm TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS payment_terms TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS incoterm TEXT;

ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS bill_date DATE;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS bill_date DATE;

ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS delivery_note_no TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS delivery_note_no TEXT;

ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS supplier_credit_note_ref TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS supplier_credit_note_ref TEXT;

ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS per_received NUMERIC NOT NULL DEFAULT 0;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS per_billed NUMERIC NOT NULL DEFAULT 0;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS per_received NUMERIC NOT NULL DEFAULT 0;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS per_billed NUMERIC NOT NULL DEFAULT 0;

ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS amended_from_doc_id BIGINT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS amended_from_doc_id BIGINT;

ALTER TABLE purchase_document_lines ADD COLUMN IF NOT EXISTS uom_factor NUMERIC;
ALTER TABLE purchase_document_lines ADD COLUMN IF NOT EXISTS line_required_by DATE;
ALTER TABLE sales_document_lines ADD COLUMN IF NOT EXISTS uom_factor NUMERIC;
ALTER TABLE sales_document_lines ADD COLUMN IF NOT EXISTS line_required_by DATE;

ALTER TABLE purchase_document_lines ADD COLUMN IF NOT EXISTS rejected_qty NUMERIC NOT NULL DEFAULT 0;
ALTER TABLE purchase_document_lines ADD COLUMN IF NOT EXISTS reject_reason TEXT;
ALTER TABLE sales_document_lines ADD COLUMN IF NOT EXISTS rejected_qty NUMERIC NOT NULL DEFAULT 0;
ALTER TABLE sales_document_lines ADD COLUMN IF NOT EXISTS reject_reason TEXT;

ALTER TABLE purchase_document_lines ADD COLUMN IF NOT EXISTS tax_code TEXT;
ALTER TABLE sales_document_lines ADD COLUMN IF NOT EXISTS tax_code TEXT;

ALTER TABLE purchase_match_policies ADD COLUMN IF NOT EXISTS receipt_over_tolerance_pct NUMERIC NOT NULL DEFAULT 0;
