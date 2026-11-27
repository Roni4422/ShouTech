-- 090 - Persist the exact source receipt line on linked return movements (PostgreSQL).
ALTER TABLE material_stock_movements
    ADD COLUMN IF NOT EXISTS original_source_document_line_id BIGINT;
CREATE INDEX IF NOT EXISTS ix_stock_movements_original_line
    ON material_stock_movements(company_id, original_source_document_line_id, stock_movement_id);
