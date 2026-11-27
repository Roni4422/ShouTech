-- ============================================================================
-- 092 - CRM opportunity items (PostgreSQL)
-- PostgreSQL counterpart of sqlite/092_crm_opportunity_items.sql
-- (sales cycle upgrade B1, gap G3). See the SQLite file for the
-- design notes: company CODE keying, audit columns, soft delete, and
-- stored line_subtotal / line_tax / line_total mirroring
-- sales_document_lines.
-- ============================================================================

CREATE TABLE IF NOT EXISTS crm_opportunity_items (
    item_id uuid NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    opportunity_id uuid NOT NULL,
    line_no INTEGER NOT NULL,
    material_id BIGINT,
    material_code TEXT,
    description TEXT NOT NULL DEFAULT '',
    qty NUMERIC NOT NULL DEFAULT 0,
    unit TEXT,
    unit_price NUMERIC NOT NULL DEFAULT 0,
    discount_pct NUMERIC NOT NULL DEFAULT 0,
    tax_rate NUMERIC NOT NULL DEFAULT 0,
    line_subtotal NUMERIC NOT NULL DEFAULT 0,
    line_tax NUMERIC NOT NULL DEFAULT 0,
    line_total NUMERIC NOT NULL DEFAULT 0,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_crm_opp_items_line
    ON crm_opportunity_items(company_id, opportunity_id, line_no) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_crm_opp_items_opp
    ON crm_opportunity_items(company_id, opportunity_id) WHERE is_deleted = FALSE;
