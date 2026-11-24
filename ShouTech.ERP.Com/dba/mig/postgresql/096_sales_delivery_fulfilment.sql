-- 096 - Sales delivery fulfilment (PostgreSQL): picking, dispatch
-- linkage + proof of delivery, delivery cost, returned deliveries.
-- Sales cycle upgrade W4 (docs/SALES_CYCLE_UPGRADE.md Part II, spec §4).
-- See the SQLite twin for the full rationale. Delivery cost is
-- RECORDING ONLY — no GL posting (accounting-policy decision pending).

CREATE TABLE IF NOT EXISTS sales_picking (
    picking_id BIGSERIAL PRIMARY KEY,
    company_id TEXT NOT NULL,
    sales_order_id BIGINT NOT NULL REFERENCES sales_documents(doc_id),
    status TEXT NOT NULL DEFAULT 'Draft' CHECK (status IN ('Draft', 'Confirmed', 'Cancelled')),
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (to_char(now(), 'YYYY-MM-DD HH24:MI:SS')),
    confirmed_by TEXT,
    confirmed_at TEXT
);
CREATE INDEX IF NOT EXISTS ix_sales_picking_order
    ON sales_picking(company_id, sales_order_id, status);

CREATE TABLE IF NOT EXISTS sales_picking_lines (
    picking_line_id BIGSERIAL PRIMARY KEY,
    picking_id BIGINT NOT NULL REFERENCES sales_picking(picking_id) ON DELETE CASCADE,
    source_line_no INTEGER NOT NULL,
    material_id BIGINT,
    material_code TEXT,
    description TEXT NOT NULL DEFAULT '',
    unit TEXT,
    ordered_qty NUMERIC NOT NULL DEFAULT 0,
    picked_qty NUMERIC NOT NULL DEFAULT 0,
    warehouse_id BIGINT,
    UNIQUE (picking_id, source_line_no)
);

ALTER TABLE delivery_requests ADD COLUMN IF NOT EXISTS sales_doc_id BIGINT;
ALTER TABLE delivery_requests ADD COLUMN IF NOT EXISTS sales_doc_kind TEXT;
ALTER TABLE delivery_requests ADD COLUMN IF NOT EXISTS vehicle_no TEXT;
ALTER TABLE delivery_requests ADD COLUMN IF NOT EXISTS delivered_at TEXT;
ALTER TABLE delivery_requests ADD COLUMN IF NOT EXISTS received_by TEXT;
ALTER TABLE delivery_requests ADD COLUMN IF NOT EXISTS signature_image BYTEA;

ALTER TABLE sales_documents
    ADD COLUMN IF NOT EXISTS delivery_cost NUMERIC NOT NULL DEFAULT 0;
