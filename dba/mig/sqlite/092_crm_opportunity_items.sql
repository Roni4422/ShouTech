-- ============================================================================
-- 092 - CRM opportunity items (SQLite)
--
-- Sales cycle upgrade B1 (gap G3): opportunities gain a real items
-- table so an opportunity -> quotation conversion carries actual
-- material lines (material, description, qty, unit, price, discount,
-- tax) instead of fabricating one title x expected-amount line.
--
-- Conventions mirror 057_crm.sql (company CODE keying, audit columns,
-- soft delete) and sales_document_lines (010): line_subtotal is the
-- NET amount after the line discount, line_tax is computed on the net
-- subtotal, line_total = line_subtotal + line_tax. All three are
-- stored (not generated) exactly like sales_document_lines so the
-- opportunity total and the quotation built from it can never drift
-- apart by a rounding-path difference.
--
-- material_id is the materials table's INTEGER identity (nullable:
-- a free-text opportunity line is legitimate before a material is
-- chosen); material_code keeps the display snapshot.
--
-- Idempotent: CREATE TABLE / INDEX IF NOT EXISTS; the checksum
-- ledger skips a re-run.
-- ============================================================================

CREATE TABLE IF NOT EXISTS crm_opportunity_items (
    item_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    opportunity_id TEXT NOT NULL,
    line_no INTEGER NOT NULL,
    material_id INTEGER,
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
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT,
    created_by TEXT,
    updated_by TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_crm_opp_items_line
    ON crm_opportunity_items(company_id, opportunity_id, line_no) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_crm_opp_items_opp
    ON crm_opportunity_items(company_id, opportunity_id) WHERE is_deleted = 0;
