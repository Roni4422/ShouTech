-- 088 - Persist the inventory / consumed-cost split for landed-cost postings (SQLite).
-- This ledger is written in the same transaction as the stock revaluation and GL entry.
CREATE TABLE IF NOT EXISTS purchase_landed_cost_stock_adjustments (
    adjustment_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    landed_cost_id TEXT NOT NULL,
    allocation_id TEXT NOT NULL,
    source_movement_id INTEGER NOT NULL,
    warehouse_id INTEGER NOT NULL,
    material_id INTEGER NOT NULL,
    allocated_base_amount NUMERIC NOT NULL,
    original_quantity NUMERIC NOT NULL CHECK (original_quantity > 0),
    remaining_quantity NUMERIC NOT NULL CHECK (remaining_quantity >= 0 AND remaining_quantity <= original_quantity),
    inventory_adjustment NUMERIC NOT NULL,
    consumed_adjustment NUMERIC NOT NULL,
    unit_cost_before NUMERIC NOT NULL,
    unit_cost_after NUMERIC NOT NULL CHECK (unit_cost_after >= 0),
    actor TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE(company_id, allocation_id),
    FOREIGN KEY(landed_cost_id) REFERENCES purchase_landed_costs(landed_cost_id),
    FOREIGN KEY(allocation_id) REFERENCES purchase_landed_cost_allocations(allocation_id)
);
CREATE INDEX IF NOT EXISTS ix_lc_stock_adj_material
    ON purchase_landed_cost_stock_adjustments(company_id, material_id, warehouse_id, created_at);
