-- 089 - Preserve the exact commercial document line on stock movements (SQLite).
-- Landed-cost allocation and traceability must target the receipt line's own cost layer.
-- @if-column-missing material_stock_movements.source_document_line_id
ALTER TABLE material_stock_movements ADD COLUMN source_document_line_id INTEGER;
-- @endif
CREATE INDEX IF NOT EXISTS ix_stock_movements_source_line
    ON material_stock_movements(company_id, source_document_line_id);
