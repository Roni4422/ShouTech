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

-- SQLite NUMERIC affinity does not enforce scale, but this documents the canonical precision.
-- Application validation remains responsible for limiting quantities to six decimal places.
