-- 089 - Preserve the exact commercial source line on stock movements (PostgreSQL).
-- SQLite counterpart: sqlite/089_sml.sql.
ALTER TABLE material_stock_movements
    ADD COLUMN IF NOT EXISTS source_document_line_id BIGINT;
CREATE INDEX IF NOT EXISTS ix_stock_movements_source_line
    ON material_stock_movements(company_id, source_document_line_id, stock_movement_id);
