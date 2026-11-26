-- 090 - Persist the exact source receipt line on linked return movements (SQLite).
-- Enables FIFO landed-cost consumption to distinguish returns of this receipt
-- from unrelated supplier returns for the same material.
-- @if-column-missing material_stock_movements.original_source_document_line_id
ALTER TABLE material_stock_movements ADD COLUMN original_source_document_line_id INTEGER;
CREATE INDEX IF NOT EXISTS ix_stock_movements_original_line
    ON material_stock_movements(company_id, original_source_document_line_id, stock_movement_id);
