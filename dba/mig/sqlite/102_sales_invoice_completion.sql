-- ============================================================================
-- 102 - Sales invoice completion (SQLite)
--
-- Owner sales spec section 5 (Sales_Cycle.txt), workstream W5: the three
-- remaining invoice gaps —
--   1. CONSOLIDATED INVOICES: one SAL invoice drawing lines from MULTIPLE
--      posted delivery notes. document_links already supports many
--      DLV -> SAL links for one invoice, but fulfilment, order progress
--      and the B4 delivery-COGS (1215) relief all matched invoice lines
--      to a source by line_no alone, which is ambiguous the moment two
--      deliveries contribute lines. sales_document_lines therefore gains
--      explicit per-line source attribution:
--        source_doc_id   — the delivery note the line was drawn from
--        source_line_no  — that delivery's line number the line draws on
--      Chain-created single-source lines keep NULL attribution and the
--      historical line_no identity; consolidated lines always carry it.
--      Every consumer (conversion remaining-qty, per-order per_billed,
--      posting-time progress refresh, 1215 relief) prefers the explicit
--      attribution when present and falls back to the legacy rule when
--      it is absent, so pre-102 documents behave exactly as before.
--   2. EXPORT INVOICE FIELDS on the sales header: is_export flags an
--      export invoice; incoterm / ports / country of origin / customs
--      reference / vessel-or-flight / gross+net weights ride alongside.
--      Stored for every sales document kind, edited on InvoiceWin for
--      sales invoices flagged as export.
--
-- Idempotent: column adds guarded by the migrator directive, index
-- CREATE IF NOT EXISTS; the checksum ledger skips a re-run.
-- ============================================================================

-- ---- Consolidated-invoice line source attribution ---------------------------
-- @if-column-missing sales_document_lines.source_doc_id
ALTER TABLE sales_document_lines ADD COLUMN source_doc_id INTEGER;
-- @if-column-missing sales_document_lines.source_line_no
ALTER TABLE sales_document_lines ADD COLUMN source_line_no INTEGER;
CREATE INDEX IF NOT EXISTS ix_sales_document_lines_source_doc
    ON sales_document_lines(source_doc_id) WHERE source_doc_id IS NOT NULL;

-- ---- Export invoice header fields --------------------------------------------
-- @if-column-missing sales_documents.is_export
ALTER TABLE sales_documents ADD COLUMN is_export INTEGER NOT NULL DEFAULT 0;
-- @if-column-missing sales_documents.incoterm
ALTER TABLE sales_documents ADD COLUMN incoterm TEXT;
-- @if-column-missing sales_documents.port_of_loading
ALTER TABLE sales_documents ADD COLUMN port_of_loading TEXT;
-- @if-column-missing sales_documents.port_of_discharge
ALTER TABLE sales_documents ADD COLUMN port_of_discharge TEXT;
-- @if-column-missing sales_documents.country_of_origin
ALTER TABLE sales_documents ADD COLUMN country_of_origin TEXT;
-- @if-column-missing sales_documents.customs_ref
ALTER TABLE sales_documents ADD COLUMN customs_ref TEXT;
-- @if-column-missing sales_documents.vessel_flight
ALTER TABLE sales_documents ADD COLUMN vessel_flight TEXT;
-- @if-column-missing sales_documents.gross_weight
ALTER TABLE sales_documents ADD COLUMN gross_weight NUMERIC;
-- @if-column-missing sales_documents.net_weight
ALTER TABLE sales_documents ADD COLUMN net_weight NUMERIC;
