-- ============================================================================
-- 053 - Stock valuation engine (PostgreSQL)
-- PostgreSQL counterpart of sqlite/053_valuation_engine.sql (Fix Phase
-- Step 6, Phase B design section 1). See the SQLite file for the design
-- notes and the numbering note (053 applies cleanly after 059-061).
-- ============================================================================

ALTER TABLE material_stock_movements ADD COLUMN IF NOT EXISTS balance_qty_after NUMERIC;
ALTER TABLE material_stock_movements ADD COLUMN IF NOT EXISTS valuation_rate NUMERIC;
ALTER TABLE material_stock_movements ADD COLUMN IF NOT EXISTS stock_value_after NUMERIC;
ALTER TABLE material_stock_movements ADD COLUMN IF NOT EXISTS cost_amount NUMERIC;
ALTER TABLE material_stock_movements ADD COLUMN IF NOT EXISTS valuation_method TEXT;
ALTER TABLE material_stock_movements ADD COLUMN IF NOT EXISTS repost_batch_id TEXT;

ALTER TABLE material_stock_balances ADD COLUMN IF NOT EXISTS stock_value NUMERIC NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS stock_cost_layers (
    layer_id BIGSERIAL PRIMARY KEY,
    company_id TEXT NOT NULL,
    warehouse_id INTEGER NOT NULL,
    material_id INTEGER NOT NULL,
    source_movement_id INTEGER,
    layer_date TEXT NOT NULL DEFAULT '',
    qty_in NUMERIC NOT NULL DEFAULT 0,
    qty_remaining NUMERIC NOT NULL DEFAULT 0,
    unit_cost NUMERIC NOT NULL DEFAULT 0,
    is_open BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_stock_cost_layers_queue
    ON stock_cost_layers(company_id, warehouse_id, material_id, is_open, layer_date, layer_id);

CREATE TABLE IF NOT EXISTS stock_revaluation_jobs (
    job_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    warehouse_id INTEGER,
    material_id INTEGER,
    from_date TEXT NOT NULL,
    reason TEXT,
    status TEXT NOT NULL DEFAULT 'Pending',
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    completed_at TIMESTAMPTZ,
    movements_replayed INTEGER NOT NULL DEFAULT 0,
    value_delta NUMERIC NOT NULL DEFAULT 0,
    error TEXT
);
CREATE INDEX IF NOT EXISTS ix_stock_revaluation_jobs_company
    ON stock_revaluation_jobs(company_id, created_at);

UPDATE material_stock_balances
SET stock_value = available_qty * average_cost
WHERE stock_value = 0 AND average_cost <> 0;

UPDATE material_stock_movements
SET valuation_rate = unit_cost,
    cost_amount = (qty_in - qty_out) * unit_cost,
    valuation_method = 'Legacy'
WHERE valuation_rate IS NULL;

INSERT INTO stock_cost_layers
    (company_id, warehouse_id, material_id, source_movement_id, layer_date,
     qty_in, qty_remaining, unit_cost, is_open)
SELECT b.company_id, b.warehouse_id, b.material_id, NULL,
       COALESCE((SELECT MAX(x.movement_date)::TEXT FROM material_stock_movements x
                  WHERE x.company_id = b.company_id
                    AND x.warehouse_id = b.warehouse_id
                    AND x.material_id = b.material_id
                    AND x.is_deleted = FALSE), CURRENT_DATE::TEXT),
       b.available_qty, b.available_qty, b.average_cost, TRUE
FROM material_stock_balances b
JOIN materials m
  ON m.company_id = b.company_id AND m.material_id = b.material_id
WHERE b.is_deleted = FALSE AND b.available_qty > 0
  AND COALESCE(m.cost_method, 'WeightedAvg') IN ('FIFO', 'LIFO')
  AND NOT EXISTS (SELECT 1 FROM stock_cost_layers l
                  WHERE l.company_id = b.company_id
                    AND l.warehouse_id = b.warehouse_id
                    AND l.material_id = b.material_id);

INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.VALUATION.VIEW','Inventory Valuation - View','INVENTORY','VALUATION','VIEW','Inventory Valuation - View','Inventory Valuation - View','View inventory valuation (Phase B 053)',340,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.VALUATION.RECALC','Inventory Valuation - Recalculate Costs','INVENTORY','VALUATION','RECALC','Inventory Valuation - Recalculate Costs','Inventory Valuation - Recalculate Costs','Run the Recalculate Costs job (Phase B 053)',341,true) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('INV.VALUATION.VIEW','INV.VALUATION.RECALC') ON CONFLICT DO NOTHING;
INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('INV.VALUATION.VIEW','INV.VALUATION.RECALC') ON CONFLICT DO NOTHING;
INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_VIEWER' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('INV.VALUATION.VIEW') ON CONFLICT DO NOTHING;
