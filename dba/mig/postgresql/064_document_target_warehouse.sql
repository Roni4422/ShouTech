-- ============================================================================
-- 064 - Document target warehouse (PostgreSQL)
-- PostgreSQL counterpart of sqlite/064_document_target_warehouse.sql
-- (Fix Phase Step 9). See the SQLite file for the full design notes:
-- sales_documents / purchase_documents gain target_warehouse_id +
-- target_warehouse_name so an InvoiceWin StockTransfer document can
-- persist its destination warehouse for the StkPost.PostTransfer engine.
-- Idempotent: ADD COLUMN IF NOT EXISTS throughout.
-- ============================================================================

ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS target_warehouse_id INTEGER;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS target_warehouse_name TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS target_warehouse_id INTEGER;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS target_warehouse_name TEXT;
