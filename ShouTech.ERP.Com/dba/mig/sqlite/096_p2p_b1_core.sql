-- 096 - Purchase-cycle completion B1 core (SQLite): P3 PO header facts,
-- P4 stored line UOM factor + per-line required-by, P5 stored
-- per_received / per_billed, Q2 rejected qty at receipt, Q3 receipt
-- delivery-note number + bill identity (I1 bill_date), I5 PRT own
-- credit-note reference, P6 amended_from link, Q4 receipt
-- over-tolerance on the purchase match policy.
-- Companion to docs/PURCHASE_CYCLE_COMPLETION.md section 2 (P/Q/I).
-- Header/line columns live on BOTH document tables because
-- OrderDocumentRepository reads/writes both through one code path;
-- they are only meaningful for purchase documents.

-- P3: payment terms + incoterm on the document header.
-- @if-column-missing purchase_documents.payment_terms
ALTER TABLE purchase_documents ADD COLUMN payment_terms TEXT;
-- @endif
-- @if-column-missing purchase_documents.incoterm
ALTER TABLE purchase_documents ADD COLUMN incoterm TEXT;
-- @endif
-- @if-column-missing sales_documents.payment_terms
ALTER TABLE sales_documents ADD COLUMN payment_terms TEXT;
-- @endif
-- @if-column-missing sales_documents.incoterm
ALTER TABLE sales_documents ADD COLUMN incoterm TEXT;
-- @endif

-- I1: supplier bill date next to the existing reference_no (mig 023).
-- @if-column-missing purchase_documents.bill_date
ALTER TABLE purchase_documents ADD COLUMN bill_date TEXT;
-- @endif
-- @if-column-missing sales_documents.bill_date
ALTER TABLE sales_documents ADD COLUMN bill_date TEXT;
-- @endif

-- Q3: supplier delivery-note number persisted on the receipt header.
-- @if-column-missing purchase_documents.delivery_note_no
ALTER TABLE purchase_documents ADD COLUMN delivery_note_no TEXT;
-- @endif
-- @if-column-missing sales_documents.delivery_note_no
ALTER TABLE sales_documents ADD COLUMN delivery_note_no TEXT;
-- @endif

-- I5: the debit note's OWN supplier credit-note reference (never copied
-- from the original invoice's reference_no).
-- @if-column-missing purchase_documents.supplier_credit_note_ref
ALTER TABLE purchase_documents ADD COLUMN supplier_credit_note_ref TEXT;
-- @endif
-- @if-column-missing sales_documents.supplier_credit_note_ref
ALTER TABLE sales_documents ADD COLUMN supplier_credit_note_ref TEXT;
-- @endif

-- P5: stored fulfilment/billing percentages on the header, maintained
-- inside the receipt / invoice / return posting transactions.
-- @if-column-missing purchase_documents.per_received
ALTER TABLE purchase_documents ADD COLUMN per_received NUMERIC NOT NULL DEFAULT 0;
-- @endif
-- @if-column-missing purchase_documents.per_billed
ALTER TABLE purchase_documents ADD COLUMN per_billed NUMERIC NOT NULL DEFAULT 0;
-- @endif
-- @if-column-missing sales_documents.per_received
ALTER TABLE sales_documents ADD COLUMN per_received NUMERIC NOT NULL DEFAULT 0;
-- @endif
-- @if-column-missing sales_documents.per_billed
ALTER TABLE sales_documents ADD COLUMN per_billed NUMERIC NOT NULL DEFAULT 0;
-- @endif

-- P6: ERPNext-style amend link — the amended copy points at the
-- cancelled original.
-- @if-column-missing purchase_documents.amended_from_doc_id
ALTER TABLE purchase_documents ADD COLUMN amended_from_doc_id INTEGER;
-- @endif
-- @if-column-missing sales_documents.amended_from_doc_id
ALTER TABLE sales_documents ADD COLUMN amended_from_doc_id INTEGER;
-- @endif

-- P4: stored UOM conversion factor per line (snapshot from the material
-- card's UnitEquivalents at entry; posting uses the STORED factor) and
-- the per-line required-by date (defaults from the header).
-- @if-column-missing purchase_document_lines.uom_factor
ALTER TABLE purchase_document_lines ADD COLUMN uom_factor NUMERIC;
-- @endif
-- @if-column-missing purchase_document_lines.line_required_by
ALTER TABLE purchase_document_lines ADD COLUMN line_required_by TEXT;
-- @endif
-- @if-column-missing sales_document_lines.uom_factor
ALTER TABLE sales_document_lines ADD COLUMN uom_factor NUMERIC;
-- @endif
-- @if-column-missing sales_document_lines.line_required_by
ALTER TABLE sales_document_lines ADD COLUMN line_required_by TEXT;
-- @endif

-- Q2: rejected quantity + reason recorded on the receipt line. Rejected
-- qty never counts as received for PO progress; it is diverted to
-- quarantine with the QC flow (Q1) so it stays returnable via RCR.
-- @if-column-missing purchase_document_lines.rejected_qty
ALTER TABLE purchase_document_lines ADD COLUMN rejected_qty NUMERIC NOT NULL DEFAULT 0;
-- @endif
-- @if-column-missing purchase_document_lines.reject_reason
ALTER TABLE purchase_document_lines ADD COLUMN reject_reason TEXT;
-- @endif
-- @if-column-missing sales_document_lines.rejected_qty
ALTER TABLE sales_document_lines ADD COLUMN rejected_qty NUMERIC NOT NULL DEFAULT 0;
-- @endif
-- @if-column-missing sales_document_lines.reject_reason
ALTER TABLE sales_document_lines ADD COLUMN reject_reason TEXT;
-- @endif

-- I2: per-line tax code on chain lines (snapshot of the tax setup the
-- manual path resolves; the tax engine validates it at posting).
-- @if-column-missing purchase_document_lines.tax_code
ALTER TABLE purchase_document_lines ADD COLUMN tax_code TEXT;
-- @endif
-- @if-column-missing sales_document_lines.tax_code
ALTER TABLE sales_document_lines ADD COLUMN tax_code TEXT;
-- @endif

-- Q4: receipt over-delivery tolerance % on the purchase match policy
-- (mig 082). Default 0 keeps the historical hard block at 100%.
-- @if-column-missing purchase_match_policies.receipt_over_tolerance_pct
ALTER TABLE purchase_match_policies ADD COLUMN receipt_over_tolerance_pct NUMERIC NOT NULL DEFAULT 0;
-- @endif
