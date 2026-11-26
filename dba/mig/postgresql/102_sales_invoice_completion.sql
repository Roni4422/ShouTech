-- ============================================================================
-- 102 - Sales invoice completion (PostgreSQL)
--
-- PostgreSQL twin of dba/mig/sqlite/102_sales_invoice_completion.sql —
-- see that file for the full design notes (consolidated invoices drawing
-- lines from multiple posted delivery notes with per-line source
-- attribution on sales_document_lines, and the export invoice header
-- fields on sales_documents). Dialect differences only: BOOLEAN flags
-- and ADD COLUMN IF NOT EXISTS.
-- ============================================================================

-- ---- Consolidated-invoice line source attribution ---------------------------
ALTER TABLE sales_document_lines ADD COLUMN IF NOT EXISTS source_doc_id BIGINT;
ALTER TABLE sales_document_lines ADD COLUMN IF NOT EXISTS source_line_no INTEGER;
CREATE INDEX IF NOT EXISTS ix_sales_document_lines_source_doc
    ON sales_document_lines(source_doc_id) WHERE source_doc_id IS NOT NULL;

-- ---- Export invoice header fields --------------------------------------------
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS is_export BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS incoterm TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS port_of_loading TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS port_of_discharge TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS country_of_origin TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS customs_ref TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS vessel_flight TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS gross_weight NUMERIC;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS net_weight NUMERIC;
