-- 089 - Preserve the exact commercial document line on stock movements (PostgreSQL).
-- Landed-cost allocation and traceability must target the receipt line's own cost layer.
ALTER TABLE material_stock_movements
    ADD COLUMN IF NOT EXISTS source_document_line_id BIGINT;
CREATE INDEX IF NOT EXISTS ix_stock_movements_source_line
    ON material_stock_movements(company_id, source_document_line_id);
