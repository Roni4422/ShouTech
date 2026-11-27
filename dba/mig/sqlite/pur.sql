-- @shoutech-migration 078_purchase_receipt_invoice_matches.sql BD8F8F889FEFD73B404A7B6635FBEAA4A4148DC11EA064B46BD571E139F52216 777643F6F07587E98D83A1A35B78E432560F027AA530B290B55FA2E3FA71EB5F 777643F6F07587E98D83A1A35B78E432560F027AA530B290B55FA2E3FA71EB5F 777643F6F07587E98D83A1A35B78E432560F027AA530B290B55FA2E3FA71EB5F
-- Line-level three-way match between purchase receipts and supplier invoices.
-- The match quantity is the authoritative quantity consumed from each receipt
-- line; cost and FX snapshots make GRNI and price variance auditable.
CREATE TABLE IF NOT EXISTS purchase_receipt_invoice_matches (
    match_id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    purchase_order_id INTEGER NOT NULL,
    receipt_doc_id INTEGER NOT NULL,
    receipt_line_id INTEGER NOT NULL,
    invoice_doc_id INTEGER NOT NULL,
    invoice_line_id INTEGER NOT NULL,
    material_id INTEGER NOT NULL,
    matched_qty NUMERIC NOT NULL CHECK (matched_qty > 0),
    receipt_unit_cost NUMERIC NOT NULL CHECK (receipt_unit_cost >= 0),
    receipt_exchange_rate NUMERIC NOT NULL CHECK (receipt_exchange_rate > 0),
    invoice_unit_cost NUMERIC NOT NULL CHECK (invoice_unit_cost >= 0),
    invoice_exchange_rate NUMERIC NOT NULL CHECK (invoice_exchange_rate > 0),
    receipt_base_amount NUMERIC NOT NULL,
    invoice_base_amount NUMERIC NOT NULL,
    variance_base_amount NUMERIC NOT NULL,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (company_id, receipt_line_id, invoice_line_id)
);

CREATE INDEX IF NOT EXISTS ix_purchase_receipt_matches_receipt
    ON purchase_receipt_invoice_matches(company_id, receipt_doc_id, receipt_line_id);
CREATE INDEX IF NOT EXISTS ix_purchase_receipt_matches_invoice
    ON purchase_receipt_invoice_matches(company_id, invoice_doc_id);
CREATE INDEX IF NOT EXISTS ix_purchase_receipt_matches_order
    ON purchase_receipt_invoice_matches(company_id, purchase_order_id, material_id);
-- @shoutech-migration 080_purchase_return_line_allocations.sql 8906333318EF7327D7B5541AF55B3F561E28C4F8A65DEE10FA7842DB0EED81B7 9F50578277D54B3D5B8E528CC8D54B1B0D78B8832B4519378BE2D32B3594E061 9F50578277D54B3D5B8E528CC8D54B1B0D78B8832B4519378BE2D32B3594E061 9F50578277D54B3D5B8E528CC8D54B1B0D78B8832B4519378BE2D32B3594E061
-- Purchase return lineage: each posted return line consumes quantity from one original invoice line.
-- Kept separate from document_links because quantity allocation is line-level and cumulative.
CREATE TABLE IF NOT EXISTS purchase_return_line_allocations (
    allocation_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    return_doc_id INTEGER NOT NULL,
    return_line_id INTEGER NOT NULL,
    original_doc_id INTEGER NOT NULL,
    original_line_id INTEGER NOT NULL,
    material_id INTEGER NOT NULL,
    returned_qty NUMERIC NOT NULL CHECK (returned_qty > 0),
    created_by TEXT NOT NULL,
    created_at TEXT NOT NULL,
    CONSTRAINT uq_purchase_return_line_allocation
        UNIQUE (company_id, return_doc_id, return_line_id)
);

CREATE INDEX IF NOT EXISTS ix_purchase_return_source_line
    ON purchase_return_line_allocations
       (company_id, original_doc_id, original_line_id);

CREATE INDEX IF NOT EXISTS ix_purchase_return_document
    ON purchase_return_line_allocations
       (company_id, return_doc_id);
-- @shoutech-migration 081_purchase_receipt_return_line_allocations.sql 6C8CD84FD77D092B56DE6FCF7AF336B81972C999FC8B11A12E7E15B082800D64 94E2AA624CAF10E259DAE52690DA44B5E828121780FDD37ABFD7C43E78169903 94E2AA624CAF10E259DAE52690DA44B5E828121780FDD37ABFD7C43E78169903 94E2AA624CAF10E259DAE52690DA44B5E828121780FDD37ABFD7C43E78169903
-- Tracks quantities returned to suppliers before a supplier invoice exists.
-- One allocation ties each posted return line to the exact original receipt line.
CREATE TABLE IF NOT EXISTS purchase_receipt_return_line_allocations (
    allocation_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    return_doc_id INTEGER NOT NULL,
    return_line_id INTEGER NOT NULL,
    original_doc_id INTEGER NOT NULL,
    original_line_id INTEGER NOT NULL,
    material_id INTEGER NOT NULL,
    returned_qty NUMERIC NOT NULL CHECK (returned_qty > 0),
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (company_id, return_doc_id, return_line_id)
);

CREATE INDEX IF NOT EXISTS ix_purchase_receipt_return_allocations_receipt_line
    ON purchase_receipt_return_line_allocations(company_id, original_doc_id, original_line_id);
CREATE INDEX IF NOT EXISTS ix_purchase_receipt_return_allocations_return_doc
    ON purchase_receipt_return_line_allocations(company_id, return_doc_id);
-- @shoutech-migration 082_purchase_three_way_match.sql 5262FEACA4C96AAAE651951C97F65DD33D24A24F9F5E339F4F115CAA9AD767B9 EDB0C6D0E5082D50C8D3CD1AE5805C279C303E97A9C6ACE7F925D8B9DCD6D9C9 EDB0C6D0E5082D50C8D3CD1AE5805C279C303E97A9C6ACE7F925D8B9DCD6D9C9 EDB0C6D0E5082D50C8D3CD1AE5805C279C303E97A9C6ACE7F925D8B9DCD6D9C9
-- 082 - Company-scoped purchase three-way match policy and immutable decisions.
-- Policies are opt-in. Absence of a row means enforcement is not configured.
-- This migration only creates persistence; enforcement is integrated separately.
CREATE TABLE IF NOT EXISTS purchase_match_policies (
    company_code TEXT PRIMARY KEY REFERENCES companies(company_code),
    is_enabled INTEGER NOT NULL DEFAULT 0 CHECK (is_enabled IN (0, 1)),
    require_receipt_match INTEGER NOT NULL DEFAULT 1 CHECK (require_receipt_match IN (0, 1)),
    price_warning_percent NUMERIC NOT NULL DEFAULT 0 CHECK (price_warning_percent >= 0),
    price_block_percent NUMERIC NOT NULL DEFAULT 0 CHECK (price_block_percent >= price_warning_percent),
    tax_warning_percent NUMERIC NOT NULL DEFAULT 0 CHECK (tax_warning_percent >= 0),
    tax_block_percent NUMERIC NOT NULL DEFAULT 0 CHECK (tax_block_percent >= tax_warning_percent),
    base_variance_warning_amount NUMERIC NOT NULL DEFAULT 0 CHECK (base_variance_warning_amount >= 0),
    base_variance_block_amount NUMERIC NOT NULL DEFAULT 0 CHECK (base_variance_block_amount >= base_variance_warning_amount),
    policy_version INTEGER NOT NULL DEFAULT 1 CHECK (policy_version > 0),
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_by TEXT,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS purchase_match_evaluations (
    evaluation_id TEXT PRIMARY KEY,
    company_code TEXT NOT NULL REFERENCES companies(company_code),
    invoice_doc_id INTEGER NOT NULL,
    evaluation_version INTEGER NOT NULL CHECK (evaluation_version > 0),
    policy_version INTEGER NOT NULL CHECK (policy_version > 0),
    decision TEXT NOT NULL CHECK (decision IN ('NotConfigured', 'Pass', 'Warning', 'Blocked')),
    reason_codes_json TEXT NOT NULL DEFAULT '[]',
    policy_snapshot_json TEXT NOT NULL,
    result_snapshot_json TEXT NOT NULL,
    evaluated_by TEXT,
    evaluated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (company_code, invoice_doc_id, evaluation_version)
);

CREATE INDEX IF NOT EXISTS ix_purchase_match_evaluations_invoice
    ON purchase_match_evaluations(company_code, invoice_doc_id, evaluation_version DESC);

CREATE TABLE IF NOT EXISTS purchase_match_overrides (
    override_id TEXT PRIMARY KEY,
    company_code TEXT NOT NULL REFERENCES companies(company_code),
    invoice_doc_id INTEGER NOT NULL,
    evaluation_id TEXT NOT NULL REFERENCES purchase_match_evaluations(evaluation_id),
    previous_decision TEXT NOT NULL CHECK (previous_decision = 'Blocked'),
    released_by TEXT NOT NULL,
    release_reason TEXT NOT NULL CHECK (length(trim(release_reason)) > 0),
    released_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS ix_purchase_match_overrides_invoice
    ON purchase_match_overrides(company_code, invoice_doc_id, released_at);

-- Evaluation and override rows are historical accounting evidence.
-- Corrections must append a new evaluation or override, never mutate history.
CREATE TRIGGER IF NOT EXISTS trg_purchase_match_evaluations_no_update
BEFORE UPDATE ON purchase_match_evaluations
BEGIN
    SELECT RAISE(ABORT, 'Purchase match evaluations are immutable.');
END;

CREATE TRIGGER IF NOT EXISTS trg_purchase_match_evaluations_no_delete
BEFORE DELETE ON purchase_match_evaluations
BEGIN
    SELECT RAISE(ABORT, 'Purchase match evaluations are immutable.');
END;

CREATE TRIGGER IF NOT EXISTS trg_purchase_match_overrides_no_update
BEFORE UPDATE ON purchase_match_overrides
BEGIN
    SELECT RAISE(ABORT, 'Purchase match overrides are immutable.');
END;

CREATE TRIGGER IF NOT EXISTS trg_purchase_match_overrides_no_delete
BEFORE DELETE ON purchase_match_overrides
BEGIN
    SELECT RAISE(ABORT, 'Purchase match overrides are immutable.');
END;
-- @shoutech-migration 083_purchase_match_permissions.sql B238A11052F92878185DDA6647BAE7689036CE0C2BA0F543DB7D8177A9D0404F 3C7A5FE8B0BB58A48B180D1A277F4859985FA4C445CFA72F5C5A39C287DB55F0 3C7A5FE8B0BB58A48B180D1A277F4859985FA4C445CFA72F5C5A39C287DB55F0 3C7A5FE8B0BB58A48B180D1A277F4859985FA4C445CFA72F5C5A39C287DB55F0
-- 083 - Dedicated permissions for purchase match policy and exception release.
-- Only administrators receive these sensitive permissions by default.
CREATE UNIQUE INDEX IF NOT EXISTS ux_purchase_match_override_evaluation
    ON purchase_match_overrides(company_code, invoice_doc_id, evaluation_id);

INSERT OR IGNORE INTO permissions
    (permission_code, permission_name, group_code, feature_code, operation_code,
     name_ar, name_en, description, display_order, is_sensitive)
VALUES
    ('PURCH.MATCH.POLICY.MANAGE', 'Purchasing - Manage Three-Way Match Policy',
     'PURCHASES', 'PURCH.MATCH', 'POLICY.MANAGE',
     'Purchasing - Manage Three-Way Match Policy',
     'Purchasing - Manage Three-Way Match Policy',
     'Configure company purchase invoice matching thresholds', 342, 1),
    ('PURCH.MATCH.OVERRIDE', 'Purchasing - Release Blocked Match',
     'PURCHASES', 'PURCH.MATCH', 'OVERRIDE',
     'Purchasing - Release Blocked Match',
     'Purchasing - Release Blocked Match',
     'Release a blocked supplier invoice match with a mandatory audit reason', 343, 1);

UPDATE permissions
SET group_code='PURCHASES', feature_code='PURCH.MATCH', operation_code='POLICY.MANAGE',
    name_ar='Purchasing - Manage Three-Way Match Policy',
    name_en='Purchasing - Manage Three-Way Match Policy',
    description='Configure company purchase invoice matching thresholds',
    display_order=342, is_sensitive=1, is_active=1
WHERE permission_code='PURCH.MATCH.POLICY.MANAGE';

UPDATE permissions
SET group_code='PURCHASES', feature_code='PURCH.MATCH', operation_code='OVERRIDE',
    name_ar='Purchasing - Release Blocked Match',
    name_en='Purchasing - Release Blocked Match',
    description='Release a blocked supplier invoice match with a mandatory audit reason',
    display_order=343, is_sensitive=1, is_active=1
WHERE permission_code='PURCH.MATCH.OVERRIDE';

INSERT OR IGNORE INTO role_permissions(role_id, permission_id)
SELECT r.role_id, p.permission_id
FROM roles r CROSS JOIN permissions p
WHERE r.role_code='ROLE_ADMINISTRATOR' AND r.is_active=1 AND p.is_active=1
  AND p.permission_code IN ('PURCH.MATCH.POLICY.MANAGE','PURCH.MATCH.OVERRIDE');
-- @shoutech-migration 084_supplier_invoice_reference_unique.sql AD2A5456956D7E869B65A2D82471FBCD05A561053FAE5584F6FBFC94E9790B5A A0E687132C06D19641129675FFC4DA85596396ABCAC6A8026862459DC3A0EDA9 A0E687132C06D19641129675FFC4DA85596396ABCAC6A8026862459DC3A0EDA9 A0E687132C06D19641129675FFC4DA85596396ABCAC6A8026862459DC3A0EDA9
-- Enforce one external supplier invoice reference per supplier and company.
-- Blank references remain optional; matching is case-insensitive and whitespace-trimmed.
CREATE UNIQUE INDEX IF NOT EXISTS ux_purchase_supplier_invoice_reference
    ON purchase_documents(company_id, party_id, lower(trim(reference_no)))
    WHERE doc_kind = 'PUR'
      AND party_id IS NOT NULL
      AND reference_no IS NOT NULL
      AND trim(reference_no) <> ''
      AND is_deleted = 0;
-- @shoutech-migration 085_purchase_requisition_approvals.sql 70CA88BC00D14D3A93A7C890D6EDEC055EACD340B9A1F68A6CAA613D4313C04B 03371C40C1F3564F04802CC921BACFB33EEA308533FB49C02F349464C2DD52CC 03371C40C1F3564F04802CC921BACFB33EEA308533FB49C02F349464C2DD52CC 03371C40C1F3564F04802CC921BACFB33EEA308533FB49C02F349464C2DD52CC
-- 085 - Sequential approvals for internal purchase requisitions (SQLite).
CREATE TABLE IF NOT EXISTS purchase_requisition_workflow (
 company_id TEXT NOT NULL, requisition_doc_id INTEGER NOT NULL,
 status TEXT NOT NULL CHECK(status IN ('Submitted','Approved','Rejected','Converted')),
 purchase_order_id INTEGER, created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP, updated_at TEXT,
 PRIMARY KEY(company_id,requisition_doc_id),
 FOREIGN KEY(requisition_doc_id) REFERENCES purchase_documents(doc_id));
CREATE TABLE IF NOT EXISTS purchase_requisition_approvals (
 approval_id INTEGER PRIMARY KEY AUTOINCREMENT, company_id TEXT NOT NULL, requisition_doc_id INTEGER NOT NULL,
 step_no INTEGER NOT NULL CHECK(step_no>0), approver_user_id TEXT NOT NULL,
 status TEXT NOT NULL DEFAULT 'Pending' CHECK(status IN ('Pending','Approved','Rejected')),
 reason TEXT, decided_at TEXT, UNIQUE(company_id,requisition_doc_id,step_no),
 FOREIGN KEY(company_id,requisition_doc_id) REFERENCES purchase_requisition_workflow(company_id,requisition_doc_id));
CREATE INDEX IF NOT EXISTS ix_purchase_requisition_approver_pending ON purchase_requisition_approvals(company_id,approver_user_id,status,step_no);
CREATE TABLE IF NOT EXISTS purchase_requisition_events (
 event_id INTEGER PRIMARY KEY AUTOINCREMENT, company_id TEXT NOT NULL, requisition_doc_id INTEGER NOT NULL,
 action TEXT NOT NULL, actor TEXT NOT NULL, reason TEXT, created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
CREATE INDEX IF NOT EXISTS ix_purchase_requisition_events_request ON purchase_requisition_events(company_id,requisition_doc_id,created_at);

INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES('PUR.REQ.VIEW','Purchase Requisition - View','PURCHASING','REQUISITION','VIEW','Purchase Requisition - View','Purchase Requisition - View','View internal purchase requisitions and approval history',520,0);
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES('PUR.REQ.EDIT','Purchase Requisition - Edit','PURCHASING','REQUISITION','EDIT','Purchase Requisition - Edit','Purchase Requisition - Edit','Create and edit internal purchase requisition drafts',521,0);
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES('PUR.REQ.APPROVE','Purchase Requisition - Approve','PURCHASING','REQUISITION','APPROVE','Purchase Requisition - Approve','Purchase Requisition - Approve','Approve or reject assigned sequential approval steps',522,1);
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES('PUR.REQ.CONVERT','Purchase Requisition - Convert','PURCHASING','REQUISITION','CONVERT','Purchase Requisition - Convert','Purchase Requisition - Convert','Convert approved requisitions into purchase-order drafts',523,1);
INSERT OR IGNORE INTO role_permissions(role_id,permission_id) SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p WHERE r.role_code='ROLE_ADMINISTRATOR' AND p.permission_code LIKE 'PUR.REQ.%';
INSERT OR IGNORE INTO role_permissions(role_id,permission_id) SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p WHERE r.role_code='ROLE_USER' AND p.permission_code IN ('PUR.REQ.VIEW','PUR.REQ.EDIT');
INSERT OR IGNORE INTO role_permissions(role_id,permission_id) SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p WHERE r.role_code='ROLE_VIEWER' AND p.permission_code='PUR.REQ.VIEW';

INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES
('PUR.REQ.VIEW','Purchase Requisition - View','PURCHASING','REQUISITION','VIEW','Purchase Requisition - View','Purchase Requisition - View','View requisitions and approval history',520,0),
('PUR.REQ.EDIT','Purchase Requisition - Edit','PURCHASING','REQUISITION','EDIT','Purchase Requisition - Edit','Purchase Requisition - Edit','Create internal purchase requisition drafts',521,0),
('PUR.REQ.APPROVE','Purchase Requisition - Approve','PURCHASING','REQUISITION','APPROVE','Purchase Requisition - Approve','Purchase Requisition - Approve','Approve or reject assigned steps',522,1),
('PUR.REQ.CONVERT','Purchase Requisition - Convert','PURCHASING','REQUISITION','CONVERT','Purchase Requisition - Convert','Purchase Requisition - Convert','Convert approved requests to purchase-order drafts',523,1);
INSERT OR IGNORE INTO role_permissions(role_id,permission_id) SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p WHERE r.role_code='ROLE_ADMINISTRATOR' AND p.permission_code LIKE 'PUR.REQ.%';
INSERT OR IGNORE INTO role_permissions(role_id,permission_id) SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p WHERE r.role_code='ROLE_USER' AND p.permission_code IN ('PUR.REQ.VIEW','PUR.REQ.EDIT');
INSERT OR IGNORE INTO role_permissions(role_id,permission_id) SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p WHERE r.role_code='ROLE_VIEWER' AND p.permission_code='PUR.REQ.VIEW';

-- A requisition can create at most one PO, even under concurrent conversion attempts.
CREATE UNIQUE INDEX IF NOT EXISTS ux_purchase_requisition_single_po_link ON document_links(company_id,source_doc_kind,source_doc_id,target_doc_kind) WHERE source_doc_kind='PRQ' AND target_doc_kind='PO';
-- @shoutech-migration 086_purchase_document_attachments.sql 90CB4826D8C358C0E9D728D138D1EFB1DAEA4EA4AED858A3BDA00BE4217CC0BD 5D59E88DC3DA7E2358F27520435D8B7C75BE9FBF3DAAE4B1BC2057A5036AE2B7 5D59E88DC3DA7E2358F27520435D8B7C75BE9FBF3DAAE4B1BC2057A5036AE2B7 5D59E88DC3DA7E2358F27520435D8B7C75BE9FBF3DAAE4B1BC2057A5036AE2B7
-- 086 - Secure metadata for purchase-document attachments (SQLite). Bytes remain outside the database.
CREATE TABLE IF NOT EXISTS purchase_document_attachments (
 attachment_id TEXT PRIMARY KEY, company_id TEXT NOT NULL, document_id INTEGER NOT NULL,
 storage_key TEXT NOT NULL UNIQUE, original_name TEXT NOT NULL, media_type TEXT NOT NULL,
 byte_length INTEGER NOT NULL CHECK(byte_length>0 AND byte_length<=26214400), sha256 TEXT NOT NULL,
 uploaded_by TEXT NOT NULL, uploaded_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
 is_deleted INTEGER NOT NULL DEFAULT 0 CHECK(is_deleted IN (0,1)), deleted_by TEXT, deleted_at TEXT);
CREATE INDEX IF NOT EXISTS ix_purchase_document_attachments_doc ON purchase_document_attachments(company_id,document_id,is_deleted,uploaded_at);
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES('PUR.ATTACH.VIEW','Purchase Attachment - View','PURCHASING','ATTACHMENT','VIEW','Purchase Attachment - View','Purchase Attachment - View','View purchase document attachments',530,0);
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES('PUR.ATTACH.UPLOAD','Purchase Attachment - Upload','PURCHASING','ATTACHMENT','UPLOAD','Purchase Attachment - Upload','Purchase Attachment - Upload','Upload purchase document attachments',531,0);
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES('PUR.ATTACH.DELETE','Purchase Attachment - Delete','PURCHASING','ATTACHMENT','DELETE','Purchase Attachment - Delete','Purchase Attachment - Delete','Soft-delete purchase document attachments',532,1);

INSERT OR IGNORE INTO role_permissions(role_id,permission_id) SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p WHERE r.role_code='ROLE_ADMINISTRATOR' AND p.permission_code IN ('PUR.ATTACH.VIEW','PUR.ATTACH.UPLOAD','PUR.ATTACH.DELETE');
INSERT OR IGNORE INTO role_permissions(role_id,permission_id) SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p WHERE r.role_code='ROLE_USER' AND p.permission_code IN ('PUR.ATTACH.VIEW','PUR.ATTACH.UPLOAD');
INSERT OR IGNORE INTO role_permissions(role_id,permission_id) SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p WHERE r.role_code='ROLE_VIEWER' AND p.permission_code='PUR.ATTACH.VIEW';
-- @shoutech-migration 087_purchase_landed_costs.sql 31838840ECAF144AF2183927725FE63B0DCD2AB3ABFB486037FDDBD6F7870B40 02590BC41ACD32A6A89A8424F3F17DE07FE8E13254BBCC20B65F9689D6ED1D11 02590BC41ACD32A6A89A8424F3F17DE07FE8E13254BBCC20B65F9689D6ED1D11 02590BC41ACD32A6A89A8424F3F17DE07FE8E13254BBCC20B65F9689D6ED1D11
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
-- @shoutech-migration 088_lcsa.sql BEBEECD1E99ED1D4EA8AA55AD5336CC0AE2E5FD36D1CDAEF8CFD624E441D6A33 E925161E225055DBD4DB9F9EACEC4B05DC49C7FE8E289710681A34FEAC8DA6C8 E925161E225055DBD4DB9F9EACEC4B05DC49C7FE8E289710681A34FEAC8DA6C8 E925161E225055DBD4DB9F9EACEC4B05DC49C7FE8E289710681A34FEAC8DA6C8
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
-- @shoutech-migration 090_lcbf.sql 1E8DDDEA6D25F3FF389474248667FAE5322D2B3B9B59BC848EA11DAFADD8C057 E7AD60375F35F2EEB25E029811DBED90864E5683A6D8EBBECBF9BE0069F2DDC5 E7AD60375F35F2EEB25E029811DBED90864E5683A6D8EBBECBF9BE0069F2DDC5 E7AD60375F35F2EEB25E029811DBED90864E5683A6D8EBBECBF9BE0069F2DDC5
-- 090 - Backfill exact source receipt lines for legacy stock movements (SQLite).
-- Only unambiguous receipt-line matches are populated; duplicate material/warehouse lines remain untouched.
WITH candidates AS (
    SELECT sm.stock_movement_id, MIN(l.line_id) AS line_id, COUNT(*) AS match_count
    FROM material_stock_movements sm
    JOIN companies co ON co.company_code = sm.company_id
    JOIN purchase_documents d
      ON d.company_id = CAST(co.company_id AS TEXT)
     AND d.doc_kind = 'RCV' AND d.doc_no = sm.document_no
    JOIN purchase_document_lines l
      ON l.doc_id = d.doc_id
     AND l.material_id = sm.material_id
     AND COALESCE(l.warehouse_id, d.warehouse_id) = sm.warehouse_id
    WHERE sm.movement_type = 'RCV'
      AND sm.source_document_line_id IS NULL
    GROUP BY sm.stock_movement_id
)
UPDATE material_stock_movements
SET source_document_line_id = (
    SELECT line_id FROM candidates
    WHERE candidates.stock_movement_id = material_stock_movements.stock_movement_id
)
WHERE stock_movement_id IN (SELECT stock_movement_id FROM candidates WHERE match_count = 1);
-- @shoutech-migration 091_prqc.sql A29C5F0A63A33B5D117CB5B776DFC060D3816950650FFD815E026CE735A87EBE 85F1BC1836D83AADF2D45585B667474CFE61D4217FE0D643B498A56A1E85ACED 85F1BC1836D83AADF2D45585B667474CFE61D4217FE0D643B498A56A1E85ACED 85F1BC1836D83AADF2D45585B667474CFE61D4217FE0D643B498A56A1E85ACED
-- 091 - Persist requisition conversion reservations without expanding the status CHECK constraint (SQLite).
-- The UI projects Approved + conversion_started_at as the transient Converting state.
-- @if-column-missing purchase_requisition_workflow.conversion_started_at
ALTER TABLE purchase_requisition_workflow ADD COLUMN conversion_started_at TEXT;
-- @endif
-- @shoutech-migration 107_prq_completion.sql 072F1564B27FDFC0FFE3D50CB5B594914F21520947E9BBB1FA9B2F6F313570B5 E15E0E1ECB71CB6539FBFE07E203254D48E5BCEA28B15623B1D5DBA29764307B E15E0E1ECB71CB6539FBFE07E203254D48E5BCEA28B15623B1D5DBA29764307B E15E0E1ECB71CB6539FBFE07E203254D48E5BCEA28B15623B1D5DBA29764307B
-- 107 - Purchase requisition completion (SQLite): R2 cancel/close states,
-- R3 approval rounds, R4 RFQ conversion target, R5 required-by date +
-- request type, R6 real department column (stops the Notes smuggling).
-- Companion to docs/PURCHASE_CYCLE_COMPLETION.md section 2 (R1-R6).

-- R5/R6: header facts on the shared document store. The columns live on
-- both document tables because OrderDocumentRepository reads/writes both
-- through one code path; they are only meaningful for PRQ documents.
-- @if-column-missing purchase_documents.required_by_date
ALTER TABLE purchase_documents ADD COLUMN required_by_date TEXT;
-- @endif
-- @if-column-missing purchase_documents.request_type
ALTER TABLE purchase_documents ADD COLUMN request_type TEXT;
-- @endif
-- @if-column-missing purchase_documents.department
ALTER TABLE purchase_documents ADD COLUMN department TEXT;
-- @endif
-- @if-column-missing sales_documents.required_by_date
ALTER TABLE sales_documents ADD COLUMN required_by_date TEXT;
-- @endif
-- @if-column-missing sales_documents.request_type
ALTER TABLE sales_documents ADD COLUMN request_type TEXT;
-- @endif
-- @if-column-missing sales_documents.department
ALTER TABLE sales_documents ADD COLUMN department TEXT;
-- @endif

-- R6: un-smuggle department values that the old window wrote into Notes
-- as a leading "Department: <value>" line. Only the trivially parseable
-- shape (prefix on the first line, header column still empty) is moved;
-- anything else is left exactly as stored.
UPDATE purchase_documents
SET department = TRIM(substr(notes, 13, instr(substr(notes, 13), char(10)) - 1)),
    notes = substr(notes, 13 + instr(substr(notes, 13), char(10)))
WHERE doc_kind = 'PRQ'
  AND department IS NULL
  AND notes LIKE 'Department: %'
  AND instr(notes, char(10)) > 0
  AND TRIM(substr(notes, 13, instr(substr(notes, 13), char(10)) - 1)) <> '';

-- R2/R3/R4: rebuild the workflow table with the Cancelled/Closed states,
-- the current approval round, and the RFQ conversion target. SQLite
-- cannot alter a CHECK constraint, so the 052 rebuild pattern is used.
CREATE TABLE purchase_requisition_workflow_new (
 company_id TEXT NOT NULL, requisition_doc_id INTEGER NOT NULL,
 status TEXT NOT NULL CHECK(status IN ('Submitted','Approved','Rejected','Converted','Cancelled','Closed')),
 purchase_order_id INTEGER, rfq_doc_id INTEGER,
 round_no INTEGER NOT NULL DEFAULT 1,
 created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP, updated_at TEXT,
 conversion_started_at TEXT,
 PRIMARY KEY(company_id,requisition_doc_id),
 FOREIGN KEY(requisition_doc_id) REFERENCES purchase_documents(doc_id));

INSERT INTO purchase_requisition_workflow_new
    (company_id, requisition_doc_id, status, purchase_order_id, created_at, updated_at, conversion_started_at)
SELECT company_id, requisition_doc_id, status, purchase_order_id, created_at, updated_at, conversion_started_at
FROM purchase_requisition_workflow;

DROP TABLE purchase_requisition_workflow;

ALTER TABLE purchase_requisition_workflow_new RENAME TO purchase_requisition_workflow;

-- R3: approvals gain the round they belong to; the uniqueness moves from
-- (company, requisition, step) to (company, requisition, round, step) so
-- a rejected requisition can be resubmitted with a fresh chain while the
-- prior round's rows stay untouched.
CREATE TABLE purchase_requisition_approvals_new (
 approval_id INTEGER PRIMARY KEY AUTOINCREMENT, company_id TEXT NOT NULL, requisition_doc_id INTEGER NOT NULL,
 round_no INTEGER NOT NULL DEFAULT 1,
 step_no INTEGER NOT NULL CHECK(step_no>0), approver_user_id TEXT NOT NULL,
 status TEXT NOT NULL DEFAULT 'Pending' CHECK(status IN ('Pending','Approved','Rejected')),
 reason TEXT, decided_at TEXT, UNIQUE(company_id,requisition_doc_id,round_no,step_no),
 FOREIGN KEY(company_id,requisition_doc_id) REFERENCES purchase_requisition_workflow(company_id,requisition_doc_id));

INSERT INTO purchase_requisition_approvals_new
    (approval_id, company_id, requisition_doc_id, round_no, step_no, approver_user_id, status, reason, decided_at)
SELECT approval_id, company_id, requisition_doc_id, 1, step_no, approver_user_id, status, reason, decided_at
FROM purchase_requisition_approvals;

DROP TABLE purchase_requisition_approvals;

ALTER TABLE purchase_requisition_approvals_new RENAME TO purchase_requisition_approvals;

CREATE INDEX IF NOT EXISTS ix_purchase_requisition_approver_pending ON purchase_requisition_approvals(company_id,approver_user_id,status,step_no);

-- R4: a requisition can create at most one RFQ, mirroring the PRQ->PO
-- single-link index from migration 085.
CREATE UNIQUE INDEX IF NOT EXISTS ux_purchase_requisition_single_rfq_link ON document_links(company_id,source_doc_kind,source_doc_id,target_doc_kind) WHERE source_doc_kind='PRQ' AND target_doc_kind='RFQ';

-- R2 RBAC: cancel and close follow the PUR.REQ.* pattern (085).
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES('PUR.REQ.CANCEL','Purchase Requisition - Cancel','PURCHASING','REQUISITION','CANCEL','Purchase Requisition - Cancel','Purchase Requisition - Cancel','Cancel a draft or in-flight purchase requisition before conversion',524,1);
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES('PUR.REQ.CLOSE','Purchase Requisition - Close','PURCHASING','REQUISITION','CLOSE','Purchase Requisition - Close','Purchase Requisition - Close','Close a fully converted purchase requisition',525,1);
INSERT OR IGNORE INTO role_permissions(role_id,permission_id) SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p WHERE r.role_code='ROLE_ADMINISTRATOR' AND p.permission_code LIKE 'PUR.REQ.%';
INSERT OR IGNORE INTO role_permissions(role_id,permission_id) SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p WHERE r.role_code='ROLE_USER' AND p.permission_code='PUR.REQ.CANCEL';
-- @shoutech-migration 108_purchase_landed_cost_completion.sql 46B475ADD6444F3F6073EA734A1A85B8F8664E3596D010A2007B57193395E0DF 041C689D68787FB7DF3A91CD1D057A6DB4C0F62EAE7461519113416FAD2CB2BA 041C689D68787FB7DF3A91CD1D057A6DB4C0F62EAE7461519113416FAD2CB2BA 041C689D68787FB7DF3A91CD1D057A6DB4C0F62EAE7461519113416FAD2CB2BA
-- 108 - Landed-cost completion (SQLite): per-charge payee + credit mode,
-- stored inventory/consumed split per allocation, multi-receipt vouchers,
-- and the dedicated Purchase Price Variance account (415).
-- PostgreSQL counterpart: postgresql/108_purchase_landed_cost_completion.sql.

-- L1/L2: a charge may be owed to a payee party other than the receipt
-- supplier (payee_party_id = parties.ledger_id; NULL = receipt supplier).
-- credit_mode: 'Payable' (default; the voucher creates the liability to
-- the payee) or 'Linked' (the liability already exists on a posted
-- supplier document — linked_document_id; posting reclasses the cost out
-- of the account that document originally debited, stored resolved in
-- linked_account_code, and creates NO new liability). 'AlreadyBooked'
-- remains in the CHECK only so drafts written by the interim 108 build
-- stay postable; the application no longer creates it.
ALTER TABLE purchase_landed_cost_charges ADD COLUMN payee_party_id INTEGER;
ALTER TABLE purchase_landed_cost_charges ADD COLUMN credit_mode TEXT NOT NULL DEFAULT 'Payable'
    CHECK (credit_mode IN ('Payable','AlreadyBooked','Linked'));
ALTER TABLE purchase_landed_cost_charges ADD COLUMN linked_document_id INTEGER
    REFERENCES purchase_documents(doc_id);
ALTER TABLE purchase_landed_cost_charges ADD COLUMN linked_account_code TEXT;
CREATE INDEX IF NOT EXISTS ix_purchase_landed_cost_charges_linked
    ON purchase_landed_cost_charges(linked_document_id);

-- L3: the inventory/consumed split computed at post time is stored on the
-- allocation itself; reversal vouchers copy it (negated) and posting
-- replays it exactly instead of recomputing from post-reversal stock.
ALTER TABLE purchase_landed_cost_allocations ADD COLUMN inventory_amount NUMERIC;
ALTER TABLE purchase_landed_cost_allocations ADD COLUMN consumed_amount NUMERIC;

-- L4: a voucher may draw lines from several receipts. The header keeps
-- receipt_doc_id as the primary (first) receipt for compatibility; this
-- link table carries every receipt the voucher draws from.
CREATE TABLE IF NOT EXISTS purchase_landed_cost_receipts (
    landed_cost_id TEXT NOT NULL,
    receipt_doc_id INTEGER NOT NULL,
    PRIMARY KEY (landed_cost_id, receipt_doc_id),
    FOREIGN KEY(landed_cost_id) REFERENCES purchase_landed_costs(landed_cost_id),
    FOREIGN KEY(receipt_doc_id) REFERENCES purchase_documents(doc_id)
);
CREATE INDEX IF NOT EXISTS ix_purchase_landed_cost_receipts_receipt
    ON purchase_landed_cost_receipts(receipt_doc_id, landed_cost_id);

-- Backfill the link table for vouchers created before this migration.
INSERT OR IGNORE INTO purchase_landed_cost_receipts (landed_cost_id, receipt_doc_id)
SELECT landed_cost_id, receipt_doc_id FROM purchase_landed_costs;

-- L5: dedicated Purchase Price Variance account (415), seeded into every
-- existing company that carries the purchase group (account 402), under
-- the same parent as 402. New companies receive it from the COA templates
-- (cod/app/src/Resources/COA/*.shc). Deterministic per-company account id
-- is not required on SQLite; the random GUID form matches migration 059.
INSERT INTO chart_of_accounts
    (account_id, company_id, account_code, account_name, name_ar, account_type,
     parent_account_id, is_postable, is_active, is_deleted)
SELECT lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' ||
       substr(lower(hex(randomblob(2))), 2) || '-a' ||
       substr(lower(hex(randomblob(2))), 2) || '-' || lower(hex(randomblob(6))),
       c.company_id,
       '415',
       'Purchase Price Variance',
       'Purchase Price Variance',
       'EXPENSE',
       (SELECT p.parent_account_id FROM chart_of_accounts p
         WHERE p.company_id = c.company_id AND p.account_code = '402'),
       1, 1, 0
FROM companies c
WHERE EXISTS (SELECT 1 FROM chart_of_accounts a
              WHERE a.company_id = c.company_id AND a.account_code = '402')
  AND NOT EXISTS (SELECT 1 FROM chart_of_accounts a
                  WHERE a.company_id = c.company_id AND a.account_code = '415');
-- @shoutech-migration 109_purchase_invoice_due_date.sql C4E9B8E8FCF93F156C6F9CA33BBD2D6B9E52C5272B0AC271542ACD7183522A39 B4DC53E341CD626C7CA983F14F32319926E5A47FE1CAD8B5A013C71F41238E63 B4DC53E341CD626C7CA983F14F32319926E5A47FE1CAD8B5A013C71F41238E63 B4DC53E341CD626C7CA983F14F32319926E5A47FE1CAD8B5A013C71F41238E63
-- 109 - Purchase invoice due date (SQLite).
-- Y3 (payable aging by due date): purchase_documents gains the due date the
-- Accounts Payable aging report buckets open invoices by. NULL on rows that
-- predate this migration; readers fall back to doc_date, and new invoices
-- are stamped at creation (doc date + supplier credit_days unless the user
-- picked an explicit due date in InvoiceWin).
-- @if-column-missing purchase_documents.due_date
ALTER TABLE purchase_documents ADD COLUMN due_date TEXT;
-- @endif

CREATE INDEX IF NOT EXISTS ix_purchase_documents_due_date
    ON purchase_documents (company_id, doc_kind, status, due_date);
-- @shoutech-migration 111_p2p_b1_core.sql 0FFE3DD82756B887CBCEB11371A5CE4E0F1D213712917B69837E3AD4FC585CD5 AE02915975D9431CA549011FD29D15C7B00085254ED82F46CEE46DACACDEF64E AE02915975D9431CA549011FD29D15C7B00085254ED82F46CEE46DACACDEF64E AE02915975D9431CA549011FD29D15C7B00085254ED82F46CEE46DACACDEF64E
-- 111 - Purchase-cycle completion B1 core (SQLite): P3 PO header facts,
-- P4 stored line UOM factor + per-line required-by, P5 stored
-- per_received / per_billed, Q2 rejected qty at receipt, Q3 receipt
-- delivery-note number + bill identity (I1 bill_date), I5 PRT own
-- credit-note reference, P6 amended_from link, Q4 receipt
-- over-tolerance on the purchase match policy.
-- Companion to docs/PURCHASE_CYCLE_COMPLETION.md section 2 (P/Q/I).
-- Header/line columns live on BOTH document tables because
-- OrderDocumentRepository reads/writes both through one code path;
-- they are only meaningful for purchase documents.

-- P3: payment terms + incoterm on the document header.
-- @if-column-missing purchase_documents.payment_terms
ALTER TABLE purchase_documents ADD COLUMN payment_terms TEXT;
-- @endif
-- @if-column-missing purchase_documents.incoterm
ALTER TABLE purchase_documents ADD COLUMN incoterm TEXT;
-- @endif
-- @if-column-missing sales_documents.payment_terms
ALTER TABLE sales_documents ADD COLUMN payment_terms TEXT;
-- @endif
-- @if-column-missing sales_documents.incoterm
ALTER TABLE sales_documents ADD COLUMN incoterm TEXT;
-- @endif

-- I1: supplier bill date next to the existing reference_no (mig 023).
-- @if-column-missing purchase_documents.bill_date
ALTER TABLE purchase_documents ADD COLUMN bill_date TEXT;
-- @endif
-- @if-column-missing sales_documents.bill_date
ALTER TABLE sales_documents ADD COLUMN bill_date TEXT;
-- @endif

-- Q3: supplier delivery-note number persisted on the receipt header.
-- @if-column-missing purchase_documents.delivery_note_no
ALTER TABLE purchase_documents ADD COLUMN delivery_note_no TEXT;
-- @endif
-- @if-column-missing sales_documents.delivery_note_no
ALTER TABLE sales_documents ADD COLUMN delivery_note_no TEXT;
-- @endif

-- I5: the debit note's OWN supplier credit-note reference (never copied
-- from the original invoice's reference_no).
-- @if-column-missing purchase_documents.supplier_credit_note_ref
ALTER TABLE purchase_documents ADD COLUMN supplier_credit_note_ref TEXT;
-- @endif
-- @if-column-missing sales_documents.supplier_credit_note_ref
ALTER TABLE sales_documents ADD COLUMN supplier_credit_note_ref TEXT;
-- @endif

-- P5: stored fulfilment/billing percentages on the header, maintained
-- inside the receipt / invoice / return posting transactions.
-- @if-column-missing purchase_documents.per_received
ALTER TABLE purchase_documents ADD COLUMN per_received NUMERIC NOT NULL DEFAULT 0;
-- @endif
-- @if-column-missing purchase_documents.per_billed
ALTER TABLE purchase_documents ADD COLUMN per_billed NUMERIC NOT NULL DEFAULT 0;
-- @endif
-- @if-column-missing sales_documents.per_received
ALTER TABLE sales_documents ADD COLUMN per_received NUMERIC NOT NULL DEFAULT 0;
-- @endif
-- @if-column-missing sales_documents.per_billed
ALTER TABLE sales_documents ADD COLUMN per_billed NUMERIC NOT NULL DEFAULT 0;
-- @endif

-- P6: ERPNext-style amend link — the amended copy points at the
-- cancelled original.
-- @if-column-missing purchase_documents.amended_from_doc_id
ALTER TABLE purchase_documents ADD COLUMN amended_from_doc_id INTEGER;
-- @endif
-- @if-column-missing sales_documents.amended_from_doc_id
ALTER TABLE sales_documents ADD COLUMN amended_from_doc_id INTEGER;
-- @endif

-- P4: stored UOM conversion factor per line (snapshot from the material
-- card's UnitEquivalents at entry; posting uses the STORED factor) and
-- the per-line required-by date (defaults from the header).
-- @if-column-missing purchase_document_lines.uom_factor
ALTER TABLE purchase_document_lines ADD COLUMN uom_factor NUMERIC;
-- @endif
-- @if-column-missing purchase_document_lines.line_required_by
ALTER TABLE purchase_document_lines ADD COLUMN line_required_by TEXT;
-- @endif
-- @if-column-missing sales_document_lines.uom_factor
ALTER TABLE sales_document_lines ADD COLUMN uom_factor NUMERIC;
-- @endif
-- @if-column-missing sales_document_lines.line_required_by
ALTER TABLE sales_document_lines ADD COLUMN line_required_by TEXT;
-- @endif

-- Q2: rejected quantity + reason recorded on the receipt line. Rejected
-- qty never counts as received for PO progress; it is diverted to
-- quarantine with the QC flow (Q1) so it stays returnable via RCR.
-- @if-column-missing purchase_document_lines.rejected_qty
ALTER TABLE purchase_document_lines ADD COLUMN rejected_qty NUMERIC NOT NULL DEFAULT 0;
-- @endif
-- @if-column-missing purchase_document_lines.reject_reason
ALTER TABLE purchase_document_lines ADD COLUMN reject_reason TEXT;
-- @endif
-- @if-column-missing sales_document_lines.rejected_qty
ALTER TABLE sales_document_lines ADD COLUMN rejected_qty NUMERIC NOT NULL DEFAULT 0;
-- @endif
-- @if-column-missing sales_document_lines.reject_reason
ALTER TABLE sales_document_lines ADD COLUMN reject_reason TEXT;
-- @endif

-- I2: per-line tax code on chain lines (snapshot of the tax setup the
-- manual path resolves; the tax engine validates it at posting).
-- @if-column-missing purchase_document_lines.tax_code
ALTER TABLE purchase_document_lines ADD COLUMN tax_code TEXT;
-- @endif
-- @if-column-missing sales_document_lines.tax_code
ALTER TABLE sales_document_lines ADD COLUMN tax_code TEXT;
-- @endif

-- Q4: receipt over-delivery tolerance % on the purchase match policy
-- (mig 082). Default 0 keeps the historical hard block at 100%.
-- @if-column-missing purchase_match_policies.receipt_over_tolerance_pct
ALTER TABLE purchase_match_policies ADD COLUMN receipt_over_tolerance_pct NUMERIC NOT NULL DEFAULT 0;
-- @endif
-- @shoutech-migration 112_rfq_invitations_backfill.sql B0760DAE8924329BE577E8BAB7B0F6B461ADFFD22170AF835BEBFD2682861E08 26A2DADFD74CFFCE55D0CA2E5C2572FDE97A09CC26D7EEFD474AB2E1DF35251C 26A2DADFD74CFFCE55D0CA2E5C2572FDE97A09CC26D7EEFD474AB2E1DF35251C 26A2DADFD74CFFCE55D0CA2E5C2572FDE97A09CC26D7EEFD474AB2E1DF35251C
-- 112 - RFQ supplier invitations (P1) + P5 backfill (SQLite).
-- Invitations record which suppliers were invited to quote against an
-- RFQ and how each responded; SQ creation flips the invitation to
-- Quoted. Dispatch by email is out of scope (no mail infrastructure
-- decision exists) — invitations are recorded and printable.

CREATE TABLE IF NOT EXISTS rfq_invitations (
    invitation_id INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    rfq_doc_id INTEGER NOT NULL REFERENCES purchase_documents(doc_id),
    party_id INTEGER NOT NULL,
    party_code TEXT,
    party_name TEXT,
    status TEXT NOT NULL DEFAULT 'Invited'
        CHECK (status IN ('Invited', 'Quoted', 'Declined')),
    invited_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    responded_at TEXT,
    created_by TEXT,
    UNIQUE (company_id, rfq_doc_id, party_id)
);

CREATE INDEX IF NOT EXISTS ix_rfq_invitations_rfq
    ON rfq_invitations (company_id, rfq_doc_id, status);

-- P5 backfill: stored per_received / per_billed for existing posted POs,
-- computed once from the linked receipt / invoice documents (net of
-- posted receipt returns). Mirrors GetProgressAsync semantics
-- (qty-weighted, capped at 100). Idempotent: recomputing yields the
-- same values, and the posting transactions maintain the columns from
-- here on.
UPDATE purchase_documents
SET per_received = CASE
        WHEN (SELECT COALESCE(SUM(qty), 0) FROM purchase_document_lines WHERE doc_id = purchase_documents.doc_id) <= 0 THEN 0
        ELSE MIN(100, ROUND(100.0 * MAX(0,
            (SELECT COALESCE(SUM(l.qty), 0)
               FROM document_links dl
               JOIN purchase_documents r ON r.doc_id = dl.target_doc_id
                    AND r.doc_kind = 'RCV' AND r.status = 'Posted' AND r.is_deleted = 0
               JOIN purchase_document_lines l ON l.doc_id = r.doc_id
              WHERE dl.company_id = purchase_documents.company_id
                AND dl.source_doc_kind = 'PO' AND dl.source_doc_id = purchase_documents.doc_id
                AND dl.target_doc_kind = 'RCV')
            -
            (SELECT COALESCE(SUM(a.returned_qty), 0)
               FROM purchase_receipt_return_line_allocations a
               JOIN purchase_documents rd ON rd.doc_id = a.return_doc_id
                    AND rd.doc_kind = 'RCR' AND rd.status = 'Posted' AND rd.is_deleted = 0
              WHERE a.company_id = purchase_documents.company_id
                AND a.original_doc_id IN (
                    SELECT dl2.target_doc_id FROM document_links dl2
                     WHERE dl2.company_id = purchase_documents.company_id
                       AND dl2.source_doc_kind = 'PO' AND dl2.source_doc_id = purchase_documents.doc_id
                       AND dl2.target_doc_kind = 'RCV'))
            ) / (SELECT SUM(qty) FROM purchase_document_lines WHERE doc_id = purchase_documents.doc_id), 2))
    END,
    per_billed = CASE
        WHEN (SELECT COALESCE(SUM(qty), 0) FROM purchase_document_lines WHERE doc_id = purchase_documents.doc_id) <= 0 THEN 0
        ELSE MIN(100, ROUND(100.0 * MAX(0,
            (SELECT COALESCE(SUM(l.qty), 0)
               FROM document_links dl
               JOIN purchase_documents r ON r.doc_id = dl.target_doc_id
                    AND r.doc_kind = 'PUR' AND r.status = 'Posted' AND r.is_deleted = 0
               JOIN purchase_document_lines l ON l.doc_id = r.doc_id
              WHERE dl.company_id = purchase_documents.company_id
                AND dl.source_doc_kind = 'PO' AND dl.source_doc_id = purchase_documents.doc_id
                AND dl.target_doc_kind = 'PUR')
            ) / (SELECT SUM(qty) FROM purchase_document_lines WHERE doc_id = purchase_documents.doc_id), 2))
    END
WHERE doc_kind = 'PO' AND is_deleted = 0;
-- @shoutech-migration 113_prt_match_unwind.sql F7CBD13DD2215C5698018828C13298D09BA3C0071EA442A0F48AC6A81899C8A3 0CD5612BF5077B8919F867C515F51D9BBD5949E4430045A02DFB23FD2837A0DD 0CD5612BF5077B8919F867C515F51D9BBD5949E4430045A02DFB23FD2837A0DD 0CD5612BF5077B8919F867C515F51D9BBD5949E4430045A02DFB23FD2837A0DD
-- 113 - PRT match unwind audit (SQLite, I4).
-- Posting a purchase return (debit note, PRT) decrements the mig-078
-- receipt<->invoice matched quantities so returned goods can be
-- re-invoiced. The exact decrement per match row is recorded here so
-- reversing the PRT re-winds EXACTLY what the posting unwound (never a
-- recomputation), mirroring the landed-cost stored-split rule (L3).

CREATE TABLE IF NOT EXISTS purchase_return_match_unwinds (
    unwind_id INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    return_doc_id INTEGER NOT NULL REFERENCES purchase_documents(doc_id),
    invoice_doc_id INTEGER NOT NULL,
    match_id TEXT NOT NULL,
    receipt_doc_id INTEGER NOT NULL,
    receipt_line_id INTEGER NOT NULL,
    invoice_line_id INTEGER NOT NULL,
    unwound_qty NUMERIC NOT NULL CHECK (unwound_qty > 0),
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_by TEXT
);

CREATE INDEX IF NOT EXISTS ix_prt_unwinds_return
    ON purchase_return_match_unwinds (company_id, return_doc_id);

CREATE INDEX IF NOT EXISTS ix_prt_unwinds_invoice
    ON purchase_return_match_unwinds (company_id, invoice_doc_id);
