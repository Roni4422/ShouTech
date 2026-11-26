-- 096 - Sales delivery fulfilment (SQLite): picking, dispatch linkage +
-- proof of delivery, delivery cost, returned deliveries.
-- Sales cycle upgrade W4 (docs/SALES_CYCLE_UPGRADE.md Part II, spec §4).
--
-- Picking: sales_picking / sales_picking_lines stage a posted sales
-- order's remaining-to-deliver quantities for the warehouse before a
-- delivery note is created. Lines carry the chain's line identity
-- (material_id + source_line_no = the order line's line_no).
--
-- Dispatch: delivery_requests (migration 036) gains a traceable link to
-- the sales delivery note it ships (sales_doc_id / sales_doc_kind), the
-- vehicle that carried it (vehicle_no, defaulted from the assigned
-- driver's card), and proof of delivery (delivered_at / received_by /
-- signature_image). The 'Returned' status joins the desk flow in code
-- (DeliveryStatuses) — no schema change needed for the status value.
--
-- Delivery cost: sales_documents.delivery_cost records what a delivery
-- note cost to ship. RECORDING ONLY — no GL posting (auto-posting the
-- cost is an accounting-policy decision, flagged in the upgrade doc).
--
-- Idempotent: CREATE TABLE IF NOT EXISTS + @if-column-missing guards;
-- the checksum ledger also skips a re-run.

CREATE TABLE IF NOT EXISTS sales_picking (
    picking_id INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    sales_order_id INTEGER NOT NULL REFERENCES sales_documents(doc_id),
    status TEXT NOT NULL DEFAULT 'Draft' CHECK (status IN ('Draft', 'Confirmed', 'Cancelled')),
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    confirmed_by TEXT,
    confirmed_at TEXT
);
CREATE INDEX IF NOT EXISTS ix_sales_picking_order
    ON sales_picking(company_id, sales_order_id, status);

CREATE TABLE IF NOT EXISTS sales_picking_lines (
    picking_line_id INTEGER PRIMARY KEY AUTOINCREMENT,
    picking_id INTEGER NOT NULL REFERENCES sales_picking(picking_id) ON DELETE CASCADE,
    source_line_no INTEGER NOT NULL,
    material_id INTEGER,
    material_code TEXT,
    description TEXT NOT NULL DEFAULT '',
    unit TEXT,
    ordered_qty NUMERIC NOT NULL DEFAULT 0,
    picked_qty NUMERIC NOT NULL DEFAULT 0,
    warehouse_id INTEGER,
    UNIQUE (picking_id, source_line_no)
);

-- @if-column-missing delivery_requests.sales_doc_id
ALTER TABLE delivery_requests ADD COLUMN sales_doc_id INTEGER;
-- @endif
-- @if-column-missing delivery_requests.sales_doc_kind
ALTER TABLE delivery_requests ADD COLUMN sales_doc_kind TEXT;
-- @endif
-- @if-column-missing delivery_requests.vehicle_no
ALTER TABLE delivery_requests ADD COLUMN vehicle_no TEXT;
-- @endif
-- @if-column-missing delivery_requests.delivered_at
ALTER TABLE delivery_requests ADD COLUMN delivered_at TEXT;
-- @endif
-- @if-column-missing delivery_requests.received_by
ALTER TABLE delivery_requests ADD COLUMN received_by TEXT;
-- @endif
-- @if-column-missing delivery_requests.signature_image
ALTER TABLE delivery_requests ADD COLUMN signature_image BLOB;
-- @endif

-- @if-column-missing sales_documents.delivery_cost
ALTER TABLE sales_documents ADD COLUMN delivery_cost NUMERIC NOT NULL DEFAULT 0;
-- @endif
