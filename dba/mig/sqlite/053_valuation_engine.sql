-- ============================================================================
-- 053 - Stock valuation engine (SQLite)
--
-- Fix Phase Step 6 / Phase B design section 1 (migration 053): ONE
-- StockValuationEngine owns every stock balance mutation. This migration
-- gives the engine its storage:
--   * material_stock_movements gains the valuation record of every
--     movement: the rate actually applied (valuation_rate), the signed
--     value of the movement (cost_amount: + receipt value, - issue COGS),
--     the balance quantity and stock value AFTER the movement
--     (balance_qty_after / stock_value_after, the ERPNext stock-ledger
--     pattern that makes as-of valuation a lookup instead of a replay),
--     a snapshot of the method used (valuation_method) and the
--     recalculation batch that last rewrote the row (repost_batch_id).
--   * material_stock_balances gains stock_value: the value of the
--     available quantity, maintained by the engine on every movement, so
--     every report reads ONE value in O(1) instead of deriving its own.
--     average_cost remains the display rate (the moving-average rate for
--     WeightedAvg; stock_value / available_qty for the layer methods).
--   * stock_cost_layers: the FIFO/LIFO layer queue per
--     (company, warehouse, material). 054 (batch/serial) extends the
--     movement contract, not this table.
--   * stock_revaluation_jobs: the Recalculate Costs job record
--     (Al-Ameen regenerate-costs / ERPNext repost pattern, made manual
--     and visible per owner decision V2).
--
-- NUMBERING NOTE: 053 was reserved for valuation while the foundations
-- chain shipped 059-061 first. The migrator applies pending migrations
-- in filename order and skips by checksum, so on a database that already
-- ran 059-061 this file is simply the one pending migration; it depends
-- only on 013 (inventory), 002-004 (roles/permissions) objects, never on
-- 059-061 objects, and is written to be safe in that position.
--
-- Idempotent: column adds are guarded by the migrator directives, table
-- and index creation is IF NOT EXISTS, every backfill is self-guarding
-- (only rows the engine has not valued yet), and the checksum ledger
-- skips a re-run.
-- ============================================================================

-- ---- Movement valuation columns -------------------------------------------
-- @if-column-missing material_stock_movements.balance_qty_after
ALTER TABLE material_stock_movements ADD COLUMN balance_qty_after NUMERIC;
-- @if-column-missing material_stock_movements.valuation_rate
ALTER TABLE material_stock_movements ADD COLUMN valuation_rate NUMERIC;
-- @if-column-missing material_stock_movements.stock_value_after
ALTER TABLE material_stock_movements ADD COLUMN stock_value_after NUMERIC;
-- @if-column-missing material_stock_movements.cost_amount
ALTER TABLE material_stock_movements ADD COLUMN cost_amount NUMERIC;
-- @if-column-missing material_stock_movements.valuation_method
ALTER TABLE material_stock_movements ADD COLUMN valuation_method TEXT;
-- @if-column-missing material_stock_movements.repost_batch_id
ALTER TABLE material_stock_movements ADD COLUMN repost_batch_id TEXT;

-- ---- Balance stock value ---------------------------------------------------
-- @if-column-missing material_stock_balances.stock_value
ALTER TABLE material_stock_balances ADD COLUMN stock_value NUMERIC NOT NULL DEFAULT 0;

-- ---- FIFO/LIFO layer queue --------------------------------------------------
CREATE TABLE IF NOT EXISTS stock_cost_layers (
    layer_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    warehouse_id INTEGER NOT NULL,
    material_id INTEGER NOT NULL,
    source_movement_id INTEGER,
    layer_date TEXT NOT NULL DEFAULT '',
    qty_in NUMERIC NOT NULL DEFAULT 0,
    qty_remaining NUMERIC NOT NULL DEFAULT 0,
    unit_cost NUMERIC NOT NULL DEFAULT 0,
    is_open INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_stock_cost_layers_queue
    ON stock_cost_layers(company_id, warehouse_id, material_id, is_open, layer_date, layer_id);

-- ---- Recalculate Costs job record -------------------------------------------
CREATE TABLE IF NOT EXISTS stock_revaluation_jobs (
    job_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    warehouse_id INTEGER,
    material_id INTEGER,
    from_date TEXT NOT NULL,
    reason TEXT,
    status TEXT NOT NULL DEFAULT 'Pending',
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    completed_at TEXT,
    movements_replayed INTEGER NOT NULL DEFAULT 0,
    value_delta NUMERIC NOT NULL DEFAULT 0,
    error TEXT
);
CREATE INDEX IF NOT EXISTS ix_stock_revaluation_jobs_company
    ON stock_revaluation_jobs(company_id, created_at);

-- ---- Backfills (self-guarding; engine rows are never rewritten) -------------
-- Balance value from the pre-engine average: the best value the old
-- writers ever maintained. Rows the engine has already valued carry a
-- non-zero stock_value (or a genuinely zero value at zero average) and
-- are untouched on any re-run.
UPDATE material_stock_balances
SET stock_value = available_qty * average_cost
WHERE stock_value = 0 AND average_cost <> 0;

-- Legacy movements: the recorded unit_cost is the only rate history
-- there is (selling price on sales, zero on chain deliveries - the
-- sweep's RC-1). Stamp it as the legacy valuation so report fallbacks
-- have one definition; Recalculate Costs rewrites these rows properly.
UPDATE material_stock_movements
SET valuation_rate = unit_cost,
    cost_amount = (qty_in - qty_out) * unit_cost,
    valuation_method = 'Legacy'
WHERE valuation_rate IS NULL;

-- Opening layer for FIFO/LIFO materials that already hold stock: one
-- layer at the balance average, dated at the material's last movement,
-- so the first engine issue has a queue to consume. Materials of other
-- methods need no layers. Guarded by NOT EXISTS so a re-run (or a
-- database the engine already serves) never duplicates a layer.
INSERT INTO stock_cost_layers
    (company_id, warehouse_id, material_id, source_movement_id, layer_date,
     qty_in, qty_remaining, unit_cost, is_open)
SELECT b.company_id, b.warehouse_id, b.material_id, NULL,
       COALESCE((SELECT MAX(x.movement_date) FROM material_stock_movements x
                  WHERE x.company_id = b.company_id
                    AND x.warehouse_id = b.warehouse_id
                    AND x.material_id = b.material_id
                    AND x.is_deleted = 0), date('now')),
       b.available_qty, b.available_qty, b.average_cost, 1
FROM material_stock_balances b
JOIN materials m
  ON m.company_id = b.company_id AND m.material_id = b.material_id
WHERE b.is_deleted = 0 AND b.available_qty > 0
  AND COALESCE(m.cost_method, 'WeightedAvg') IN ('FIFO', 'LIFO')
  AND NOT EXISTS (SELECT 1 FROM stock_cost_layers l
                  WHERE l.company_id = b.company_id
                    AND l.warehouse_id = b.warehouse_id
                    AND l.material_id = b.material_id);

-- ---- Permissions (028/038 pattern) -------------------------------------------
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.VALUATION.VIEW','Inventory Valuation - View','INVENTORY','VALUATION','VIEW','Inventory Valuation - View','Inventory Valuation - View','View inventory valuation (Phase B 053)',340,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='VALUATION',operation_code='VIEW',name_ar='Inventory Valuation - View',name_en='Inventory Valuation - View',display_order=340,is_sensitive=0 WHERE permission_code='INV.VALUATION.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.VALUATION.RECALC','Inventory Valuation - Recalculate Costs','INVENTORY','VALUATION','RECALC','Inventory Valuation - Recalculate Costs','Inventory Valuation - Recalculate Costs','Run the Recalculate Costs job (Phase B 053)',341,1);
UPDATE permissions SET group_code='INVENTORY',feature_code='VALUATION',operation_code='RECALC',name_ar='Inventory Valuation - Recalculate Costs',name_en='Inventory Valuation - Recalculate Costs',display_order=341,is_sensitive=1 WHERE permission_code='INV.VALUATION.RECALC';

INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('INV.VALUATION.VIEW','INV.VALUATION.RECALC');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('INV.VALUATION.VIEW','INV.VALUATION.RECALC');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_VIEWER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('INV.VALUATION.VIEW');
