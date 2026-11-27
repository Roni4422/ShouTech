-- ============================================================================
-- 064 - Document target warehouse (SQLite)
--
-- Fix Phase Step 9 (sweep invoicing-posting.md I-6, FIX_PHASE_STEP5.md
-- flag F-1): InvoiceWin's StockTransfer mode posts through the dedicated
-- two-sided engine (StkPost.PostTransfer), which needs a DESTINATION
-- warehouse. The document model has carried one for years
-- (InvDoc.TargetWarehouseId) but no column ever existed for it, so the
-- destination could not persist -- a transfer draft saved and reopened
-- lost its destination, violating the standing full-persistence rule.
-- This migration adds the target warehouse id + display name to both
-- commercial document tables so a transfer document round-trips whole.
-- No other document kind uses the columns; they stay NULL there.
--
-- Idempotent: column adds are guarded by the migrator directives; the
-- checksum ledger also skips a re-run.
-- ============================================================================

-- @if-column-missing sales_documents.target_warehouse_id
ALTER TABLE sales_documents ADD COLUMN target_warehouse_id INTEGER;
-- @if-column-missing sales_documents.target_warehouse_name
ALTER TABLE sales_documents ADD COLUMN target_warehouse_name TEXT;
-- @if-column-missing purchase_documents.target_warehouse_id
ALTER TABLE purchase_documents ADD COLUMN target_warehouse_id INTEGER;
-- @if-column-missing purchase_documents.target_warehouse_name
ALTER TABLE purchase_documents ADD COLUMN target_warehouse_name TEXT;
