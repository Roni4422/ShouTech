-- 074 - Persist per-line inclusive/exclusive tax calculation mode (SQLite).
-- @if-column-missing sales_document_lines.tax_calculation_mode
ALTER TABLE sales_document_lines
    ADD COLUMN tax_calculation_mode TEXT NOT NULL DEFAULT 'Exclusive'
    CHECK (tax_calculation_mode IN ('Exclusive', 'Inclusive'));
-- @if-column-missing purchase_document_lines.tax_calculation_mode
ALTER TABLE purchase_document_lines
    ADD COLUMN tax_calculation_mode TEXT NOT NULL DEFAULT 'Exclusive'
    CHECK (tax_calculation_mode IN ('Exclusive', 'Inclusive'));
