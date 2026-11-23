-- 089 - Preserve the exact commercial source line on stock movements (SQLite).
-- Required for landed-cost matching, returns, and traceable receipt valuation.
-- @if-column-missing material_stock_movements.source_document_line_id
ALTER TABLE material_stock_movements ADD COLUMN source_document_line_id INTEGER;
CREATE INDEX IF NOT EXISTS ix_stock_movements_source_line
    ON material_stock_movements(company_id, source_document_line_id, stock_movement_id);
