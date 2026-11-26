-- Query-performance indexes for the shared inventory ledger.
-- This migration is additive and preserves all existing business rows.
CREATE INDEX IF NOT EXISTS ix_products_company_name
    ON products(company_id, product_code, product_id);

CREATE INDEX IF NOT EXISTS ix_products_company_category
    ON products(company_id, category_id, is_deleted);

CREATE INDEX IF NOT EXISTS ix_stock_movements_company_product_date
    ON stock_movements(company_id, product_id, movement_date, movement_id);

CREATE INDEX IF NOT EXISTS ix_stock_movements_company_type_date
    ON stock_movements(company_id, movement_type, movement_date);

CREATE INDEX IF NOT EXISTS ix_stock_balances_company_warehouse_active
    ON stock_balances(company_id, warehouse_id, is_deleted, product_id);

-- Standardize inventory quantities to six decimal places for fractional units.
ALTER TABLE stock_balances
    ALTER COLUMN available_qty TYPE numeric(18,6) USING available_qty::numeric(18,6);

ALTER TABLE stock_movements
    ALTER COLUMN quantity TYPE numeric(18,6) USING quantity::numeric(18,6);
