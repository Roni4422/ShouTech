-- 087 - Landed cost voucher and line allocation ledger (SQLite).
-- Posting is append-only: posted vouchers are corrected by reversal vouchers.
CREATE TABLE IF NOT EXISTS purchase_landed_costs (
    landed_cost_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    doc_no TEXT NOT NULL,
    doc_date TEXT NOT NULL,
    receipt_doc_id INTEGER NOT NULL,
    supplier_id INTEGER,
    supplier_name TEXT,
    currency_code TEXT NOT NULL,
    exchange_rate NUMERIC NOT NULL CHECK (exchange_rate > 0),
    total_amount NUMERIC NOT NULL DEFAULT 0,
    total_base_amount NUMERIC NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Draft' CHECK (status IN ('Draft','Posted','Cancelled')),
    journal_entry_id TEXT,
    source_reversal_id TEXT,
    created_by TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    posted_by TEXT,
    posted_at TEXT,
    UNIQUE(company_id, doc_no),
    FOREIGN KEY(receipt_doc_id) REFERENCES purchase_documents(doc_id)
);
CREATE INDEX IF NOT EXISTS ix_purchase_landed_cost_receipt
    ON purchase_landed_costs(company_id, receipt_doc_id, status);

CREATE TABLE IF NOT EXISTS purchase_landed_cost_charges (
    charge_id TEXT NOT NULL PRIMARY KEY,
    landed_cost_id TEXT NOT NULL,
    charge_code TEXT NOT NULL,
    description TEXT NOT NULL,
    allocation_basis TEXT NOT NULL CHECK (allocation_basis IN ('Value','Quantity','Weight','Volume','Manual')),
    amount NUMERIC NOT NULL,
    base_amount NUMERIC NOT NULL,
    expense_account_code TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY(landed_cost_id) REFERENCES purchase_landed_costs(landed_cost_id)
);
CREATE INDEX IF NOT EXISTS ix_purchase_landed_cost_charges_header
    ON purchase_landed_cost_charges(landed_cost_id);

CREATE TABLE IF NOT EXISTS purchase_landed_cost_allocations (
    allocation_id TEXT NOT NULL PRIMARY KEY,
    landed_cost_id TEXT NOT NULL,
    charge_id TEXT NOT NULL,
    company_id TEXT NOT NULL,
    receipt_doc_id INTEGER NOT NULL,
    receipt_line_id INTEGER NOT NULL,
    material_id INTEGER NOT NULL,
    warehouse_id INTEGER,
    base_quantity NUMERIC NOT NULL CHECK (base_quantity > 0),
    allocated_base_amount NUMERIC NOT NULL,
    unit_cost_addition NUMERIC NOT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE(charge_id, receipt_line_id),
    FOREIGN KEY(landed_cost_id) REFERENCES purchase_landed_costs(landed_cost_id),
    FOREIGN KEY(charge_id) REFERENCES purchase_landed_cost_charges(charge_id)
);
CREATE INDEX IF NOT EXISTS ix_purchase_landed_cost_allocations_receipt
    ON purchase_landed_cost_allocations(company_id, receipt_doc_id, receipt_line_id);

CREATE TABLE IF NOT EXISTS purchase_landed_cost_events (
    event_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    landed_cost_id TEXT NOT NULL,
    action TEXT NOT NULL,
    actor TEXT NOT NULL,
    reason TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_purchase_landed_cost_events_header
    ON purchase_landed_cost_events(company_id, landed_cost_id, created_at);

INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES
('PUR.LANDEDCOST.VIEW','Purchase Landed Cost - View','PURCHASING','LANDEDCOST','VIEW','Purchase Landed Cost - View','Purchase Landed Cost - View','View landed-cost vouchers and allocations',540,0),
('PUR.LANDEDCOST.EDIT','Purchase Landed Cost - Edit','PURCHASING','LANDEDCOST','EDIT','Purchase Landed Cost - Edit','Purchase Landed Cost - Edit','Create and edit landed-cost drafts',541,0),
('PUR.LANDEDCOST.POST','Purchase Landed Cost - Post','PURCHASING','LANDEDCOST','POST','Purchase Landed Cost - Post','Purchase Landed Cost - Post','Post landed costs to inventory valuation and general ledger',542,1),
('PUR.LANDEDCOST.REVERSE','Purchase Landed Cost - Reverse','PURCHASING','LANDEDCOST','REVERSE','Purchase Landed Cost - Reverse','Purchase Landed Cost - Reverse','Reverse posted landed-cost vouchers with audit',543,1);
INSERT OR IGNORE INTO role_permissions(role_id,permission_id)
SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code='ROLE_ADMINISTRATOR' AND p.permission_code LIKE 'PUR.LANDEDCOST.%';
INSERT OR IGNORE INTO role_permissions(role_id,permission_id)
SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code='ROLE_USER' AND p.permission_code IN ('PUR.LANDEDCOST.VIEW','PUR.LANDEDCOST.EDIT');
INSERT OR IGNORE INTO role_permissions(role_id,permission_id)
SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code='ROLE_VIEWER' AND p.permission_code='PUR.LANDEDCOST.VIEW';
