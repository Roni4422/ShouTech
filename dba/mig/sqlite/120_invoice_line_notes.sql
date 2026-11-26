-- 120 - Per-line notes on commercial documents (invoice family
-- batch INV1, owner reference 2026-11-24 16:06: the Phenix invoice
-- grid carries a line-notes column). One design for all invoice
-- types: the notes ride on both line tables and surface in
-- InvoiceWin as an optional column (column chooser, per-user).

-- @if-column-missing sales_document_lines.notes
ALTER TABLE sales_document_lines ADD COLUMN notes TEXT;
-- @if-column-missing purchase_document_lines.notes
ALTER TABLE purchase_document_lines ADD COLUMN notes TEXT;
