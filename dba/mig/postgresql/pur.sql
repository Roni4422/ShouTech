-- @shoutech-migration 078_purchase_receipt_invoice_matches.sql 698760CC3CE59C3CB079B48FD5D3954501958F5384FA5F21A1AB0A7141D1F182 DC791C05B747C57D3819B234E325BFEEEAD3EC7ACE74D21638A2C4CA95612D75 DC791C05B747C57D3819B234E325BFEEEAD3EC7ACE74D21638A2C4CA95612D75 DC791C05B747C57D3819B234E325BFEEEAD3EC7ACE74D21638A2C4CA95612D75
-- PostgreSQL counterpart of sqlite/078_purchase_receipt_invoice_matches.sql.
-- Cost and FX snapshots make GRNI clearing and purchase-price variance auditable.
CREATE TABLE IF NOT EXISTS purchase_receipt_invoice_matches (
    match_id UUID PRIMARY KEY,
    company_id TEXT NOT NULL,
    purchase_order_id BIGINT NOT NULL,
    receipt_doc_id BIGINT NOT NULL,
    receipt_line_id BIGINT NOT NULL,
    invoice_doc_id BIGINT NOT NULL,
    invoice_line_id BIGINT NOT NULL,
    material_id BIGINT NOT NULL,
    matched_qty NUMERIC NOT NULL CHECK (matched_qty > 0),
    receipt_unit_cost NUMERIC NOT NULL CHECK (receipt_unit_cost >= 0),
    receipt_exchange_rate NUMERIC NOT NULL CHECK (receipt_exchange_rate > 0),
    invoice_unit_cost NUMERIC NOT NULL CHECK (invoice_unit_cost >= 0),
    invoice_exchange_rate NUMERIC NOT NULL CHECK (invoice_exchange_rate > 0),
    receipt_base_amount NUMERIC NOT NULL,
    invoice_base_amount NUMERIC NOT NULL,
    variance_base_amount NUMERIC NOT NULL,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (company_id, receipt_line_id, invoice_line_id)
);

CREATE INDEX IF NOT EXISTS ix_purchase_receipt_matches_receipt
    ON purchase_receipt_invoice_matches(company_id, receipt_doc_id, receipt_line_id);
CREATE INDEX IF NOT EXISTS ix_purchase_receipt_matches_invoice
    ON purchase_receipt_invoice_matches(company_id, invoice_doc_id);
CREATE INDEX IF NOT EXISTS ix_purchase_receipt_matches_order
    ON purchase_receipt_invoice_matches(company_id, purchase_order_id, material_id);
-- @shoutech-migration 080_purchase_return_line_allocations.sql 3DC9BA6A4D5D8D888E5756AB347BE877EC18324096120BFB53E250561D1A74E8 DFEAABF49032B52B091D63531B99D6FDFFB9BD9F2A8335CEDDF8C4B74AC03C8C DFEAABF49032B52B091D63531B99D6FDFFB9BD9F2A8335CEDDF8C4B74AC03C8C DFEAABF49032B52B091D63531B99D6FDFFB9BD9F2A8335CEDDF8C4B74AC03C8C
-- Purchase return lineage: each posted return line consumes quantity from one original invoice line.
-- Kept separate from document_links because quantity allocation is line-level and cumulative.
CREATE TABLE IF NOT EXISTS purchase_return_line_allocations (
    allocation_id UUID NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    return_doc_id BIGINT NOT NULL,
    return_line_id BIGINT NOT NULL,
    original_doc_id BIGINT NOT NULL,
    original_line_id BIGINT NOT NULL,
    material_id BIGINT NOT NULL,
    returned_qty NUMERIC(20, 6) NOT NULL CHECK (returned_qty > 0),
    created_by TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT uq_purchase_return_line_allocation
        UNIQUE (company_id, return_doc_id, return_line_id)
);

CREATE INDEX IF NOT EXISTS ix_purchase_return_source_line
    ON purchase_return_line_allocations
       (company_id, original_doc_id, original_line_id);

CREATE INDEX IF NOT EXISTS ix_purchase_return_document
    ON purchase_return_line_allocations
       (company_id, return_doc_id);
-- @shoutech-migration 081_purchase_receipt_return_line_allocations.sql 5B21A7AF7949BD5D062145018169E1804149BA788023AE4CF0E5D2E1FB09ED27 A293D57A541D28A236C37F141F94C5D38FC00958103B45E806A7412A1A24AD3B A293D57A541D28A236C37F141F94C5D38FC00958103B45E806A7412A1A24AD3B A293D57A541D28A236C37F141F94C5D38FC00958103B45E806A7412A1A24AD3B
-- PostgreSQL counterpart of sqlite/081_purchase_receipt_return_line_allocations.sql.
CREATE TABLE IF NOT EXISTS purchase_receipt_return_line_allocations (
    allocation_id UUID PRIMARY KEY,
    company_id TEXT NOT NULL,
    return_doc_id BIGINT NOT NULL,
    return_line_id BIGINT NOT NULL,
    original_doc_id BIGINT NOT NULL,
    original_line_id BIGINT NOT NULL,
    material_id BIGINT NOT NULL,
    returned_qty NUMERIC NOT NULL CHECK (returned_qty > 0),
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (company_id, return_doc_id, return_line_id)
);

CREATE INDEX IF NOT EXISTS ix_purchase_receipt_return_allocations_receipt_line
    ON purchase_receipt_return_line_allocations(company_id, original_doc_id, original_line_id);
CREATE INDEX IF NOT EXISTS ix_purchase_receipt_return_allocations_return_doc
    ON purchase_receipt_return_line_allocations(company_id, return_doc_id);
-- @shoutech-migration 082_purchase_three_way_match.sql E287FE601D6DF0BC3254F8422BA035687BFE2DFACB9A55CD341E3012A9F61B3B 3703321D0B22194663B5B6C019A7C7F0548544FA4519D751A8DCAD296DF07693 3703321D0B22194663B5B6C019A7C7F0548544FA4519D751A8DCAD296DF07693 3703321D0B22194663B5B6C019A7C7F0548544FA4519D751A8DCAD296DF07693
-- 082 - Company-scoped purchase three-way match policy and immutable decisions.
-- Policies are opt-in. Absence of a row means enforcement is not configured.
-- This migration only creates persistence; enforcement is integrated separately.
CREATE TABLE IF NOT EXISTS purchase_match_policies (
    company_code varchar(50) PRIMARY KEY REFERENCES companies(company_code),
    is_enabled boolean NOT NULL DEFAULT false,
    require_receipt_match boolean NOT NULL DEFAULT true,
    price_warning_percent numeric(12, 6) NOT NULL DEFAULT 0 CHECK (price_warning_percent >= 0),
    price_block_percent numeric(12, 6) NOT NULL DEFAULT 0 CHECK (price_block_percent >= price_warning_percent),
    tax_warning_percent numeric(12, 6) NOT NULL DEFAULT 0 CHECK (tax_warning_percent >= 0),
    tax_block_percent numeric(12, 6) NOT NULL DEFAULT 0 CHECK (tax_block_percent >= tax_warning_percent),
    base_variance_warning_amount numeric(20, 6) NOT NULL DEFAULT 0 CHECK (base_variance_warning_amount >= 0),
    base_variance_block_amount numeric(20, 6) NOT NULL DEFAULT 0 CHECK (base_variance_block_amount >= base_variance_warning_amount),
    policy_version integer NOT NULL DEFAULT 1 CHECK (policy_version > 0),
    created_by varchar(100),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_by varchar(100),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS purchase_match_evaluations (
    evaluation_id uuid PRIMARY KEY,
    company_code varchar(50) NOT NULL REFERENCES companies(company_code),
    invoice_doc_id bigint NOT NULL,
    evaluation_version integer NOT NULL CHECK (evaluation_version > 0),
    policy_version integer NOT NULL CHECK (policy_version > 0),
    decision varchar(20) NOT NULL CHECK (decision IN ('NotConfigured', 'Pass', 'Warning', 'Blocked')),
    reason_codes_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    policy_snapshot_json jsonb NOT NULL,
    result_snapshot_json jsonb NOT NULL,
    evaluated_by varchar(100),
    evaluated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (company_code, invoice_doc_id, evaluation_version)
);

CREATE INDEX IF NOT EXISTS ix_purchase_match_evaluations_invoice
    ON purchase_match_evaluations(company_code, invoice_doc_id, evaluation_version DESC);

CREATE TABLE IF NOT EXISTS purchase_match_overrides (
    override_id uuid PRIMARY KEY,
    company_code varchar(50) NOT NULL REFERENCES companies(company_code),
    invoice_doc_id bigint NOT NULL,
    evaluation_id uuid NOT NULL REFERENCES purchase_match_evaluations(evaluation_id),
    previous_decision varchar(20) NOT NULL CHECK (previous_decision = 'Blocked'),
    released_by varchar(100) NOT NULL,
    release_reason text NOT NULL CHECK (length(trim(release_reason)) > 0),
    released_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_purchase_match_overrides_invoice
    ON purchase_match_overrides(company_code, invoice_doc_id, released_at);

-- Evaluation and override rows are historical accounting evidence.
-- Corrections must append a new evaluation or override, never mutate history.
CREATE OR REPLACE FUNCTION reject_purchase_match_history_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Purchase match history is immutable.';
END;
$$;

DROP TRIGGER IF EXISTS trg_purchase_match_evaluations_immutable ON purchase_match_evaluations;
CREATE TRIGGER trg_purchase_match_evaluations_immutable
BEFORE UPDATE OR DELETE ON purchase_match_evaluations
FOR EACH ROW EXECUTE FUNCTION reject_purchase_match_history_mutation();

DROP TRIGGER IF EXISTS trg_purchase_match_overrides_immutable ON purchase_match_overrides;
CREATE TRIGGER trg_purchase_match_overrides_immutable
BEFORE UPDATE OR DELETE ON purchase_match_overrides
FOR EACH ROW EXECUTE FUNCTION reject_purchase_match_history_mutation();
-- @shoutech-migration 083_purchase_match_permissions.sql F76BEF9208BCC795D25CC703BCD7A8370D38B0722CF3D0A5857628D391D19916 5ED99ECC32218D07A66649BF46F84D89150BA77C330801F8CB5DFAC3CC89C280 5ED99ECC32218D07A66649BF46F84D89150BA77C330801F8CB5DFAC3CC89C280 5ED99ECC32218D07A66649BF46F84D89150BA77C330801F8CB5DFAC3CC89C280
-- 083 - Dedicated permissions for purchase match policy and exception release.
-- Only administrators receive these sensitive permissions by default.
CREATE UNIQUE INDEX IF NOT EXISTS ux_purchase_match_override_evaluation
    ON purchase_match_overrides(company_code, invoice_doc_id, evaluation_id);

INSERT INTO permissions
    (permission_code, permission_name, group_code, feature_code, operation_code,
     name_ar, name_en, description, display_order, is_sensitive)
VALUES
    ('PURCH.MATCH.POLICY.MANAGE', 'Purchasing - Manage Three-Way Match Policy',
     'PURCHASES', 'PURCH.MATCH', 'POLICY.MANAGE',
     'Purchasing - Manage Three-Way Match Policy',
     'Purchasing - Manage Three-Way Match Policy',
     'Configure company purchase invoice matching thresholds', 342, true),
    ('PURCH.MATCH.OVERRIDE', 'Purchasing - Release Blocked Match',
     'PURCHASES', 'PURCH.MATCH', 'OVERRIDE',
     'Purchasing - Release Blocked Match',
     'Purchasing - Release Blocked Match',
     'Release a blocked supplier invoice match with a mandatory audit reason', 343, true)
ON CONFLICT(permission_code) DO UPDATE SET
    permission_name=EXCLUDED.permission_name,
    group_code=EXCLUDED.group_code,
    feature_code=EXCLUDED.feature_code,
    operation_code=EXCLUDED.operation_code,
    name_ar=EXCLUDED.name_ar,
    name_en=EXCLUDED.name_en,
    description=EXCLUDED.description,
    display_order=EXCLUDED.display_order,
    is_sensitive=EXCLUDED.is_sensitive,
    is_active=true;

INSERT INTO role_permissions(role_id, permission_id)
SELECT r.role_id, p.permission_id
FROM roles r CROSS JOIN permissions p
WHERE r.role_code='ROLE_ADMINISTRATOR' AND r.is_active=true AND p.is_active=true
  AND p.permission_code IN ('PURCH.MATCH.POLICY.MANAGE','PURCH.MATCH.OVERRIDE')
ON CONFLICT DO NOTHING;
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
-- @shoutech-migration 085_purchase_requisition_approvals.sql 9E9DF8966C7D58432F7684E97B69C3BAF442CE51CF064F64DF43A883F2D42FC6 68EC22EFD0902C50CB104283B654FFA04874192919D6FA0ECA041D69EB82BF62 68EC22EFD0902C50CB104283B654FFA04874192919D6FA0ECA041D69EB82BF62 68EC22EFD0902C50CB104283B654FFA04874192919D6FA0ECA041D69EB82BF62
-- 085 - Sequential approvals for internal purchase requisitions (PostgreSQL).
CREATE TABLE IF NOT EXISTS purchase_requisition_workflow (
 company_id UUID NOT NULL, requisition_doc_id BIGINT NOT NULL,
 status TEXT NOT NULL CHECK(status IN ('Submitted','Approved','Rejected','Converted')),
 purchase_order_id BIGINT, created_at TIMESTAMPTZ NOT NULL DEFAULT now(), updated_at TIMESTAMPTZ,
 PRIMARY KEY(company_id,requisition_doc_id),
 FOREIGN KEY(requisition_doc_id) REFERENCES purchase_documents(doc_id));
CREATE TABLE IF NOT EXISTS purchase_requisition_approvals (
 approval_id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, company_id UUID NOT NULL, requisition_doc_id BIGINT NOT NULL,
 step_no INTEGER NOT NULL CHECK(step_no>0), approver_user_id TEXT NOT NULL,
 status TEXT NOT NULL DEFAULT 'Pending' CHECK(status IN ('Pending','Approved','Rejected')),
 reason TEXT, decided_at TIMESTAMPTZ, UNIQUE(company_id,requisition_doc_id,step_no),
 FOREIGN KEY(company_id,requisition_doc_id) REFERENCES purchase_requisition_workflow(company_id,requisition_doc_id));
CREATE INDEX IF NOT EXISTS ix_purchase_requisition_approver_pending ON purchase_requisition_approvals(company_id,approver_user_id,status,step_no);
CREATE TABLE IF NOT EXISTS purchase_requisition_events (
 event_id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, company_id UUID NOT NULL, requisition_doc_id BIGINT NOT NULL,
 action TEXT NOT NULL, actor TEXT NOT NULL, reason TEXT, created_at TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE INDEX IF NOT EXISTS ix_purchase_requisition_events_request ON purchase_requisition_events(company_id,requisition_doc_id,created_at);

INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES('PUR.REQ.VIEW','Purchase Requisition - View','PURCHASING','REQUISITION','VIEW','Purchase Requisition - View','Purchase Requisition - View','View internal purchase requisitions and approval history',520,false) ON CONFLICT(permission_code) DO NOTHING;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES('PUR.REQ.EDIT','Purchase Requisition - Edit','PURCHASING','REQUISITION','EDIT','Purchase Requisition - Edit','Purchase Requisition - Edit','Create and edit internal purchase requisition drafts',521,false) ON CONFLICT(permission_code) DO NOTHING;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES('PUR.REQ.APPROVE','Purchase Requisition - Approve','PURCHASING','REQUISITION','APPROVE','Purchase Requisition - Approve','Purchase Requisition - Approve','Approve or reject assigned sequential approval steps',522,true) ON CONFLICT(permission_code) DO NOTHING;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES('PUR.REQ.CONVERT','Purchase Requisition - Convert','PURCHASING','REQUISITION','CONVERT','Purchase Requisition - Convert','Purchase Requisition - Convert','Convert approved requisitions into purchase-order drafts',523,true) ON CONFLICT(permission_code) DO NOTHING;
INSERT INTO role_permissions(role_id,permission_id) SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p WHERE r.role_code='ROLE_ADMINISTRATOR' AND p.permission_code LIKE 'PUR.REQ.%' ON CONFLICT DO NOTHING;
INSERT INTO role_permissions(role_id,permission_id) SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p WHERE r.role_code='ROLE_USER' AND p.permission_code IN ('PUR.REQ.VIEW','PUR.REQ.EDIT') ON CONFLICT DO NOTHING;
INSERT INTO role_permissions(role_id,permission_id) SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p WHERE r.role_code='ROLE_VIEWER' AND p.permission_code='PUR.REQ.VIEW' ON CONFLICT DO NOTHING;

INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES
('PUR.REQ.VIEW','Purchase Requisition - View','PURCHASING','REQUISITION','VIEW','Purchase Requisition - View','Purchase Requisition - View','View requisitions and approval history',520,false),
('PUR.REQ.EDIT','Purchase Requisition - Edit','PURCHASING','REQUISITION','EDIT','Purchase Requisition - Edit','Purchase Requisition - Edit','Create internal purchase requisition drafts',521,false),
('PUR.REQ.APPROVE','Purchase Requisition - Approve','PURCHASING','REQUISITION','APPROVE','Purchase Requisition - Approve','Purchase Requisition - Approve','Approve or reject assigned steps',522,true),
('PUR.REQ.CONVERT','Purchase Requisition - Convert','PURCHASING','REQUISITION','CONVERT','Purchase Requisition - Convert','Purchase Requisition - Convert','Convert approved requests to purchase-order drafts',523,true)
ON CONFLICT(permission_code) DO NOTHING;
INSERT INTO role_permissions(role_id,permission_id) SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p WHERE r.role_code='ROLE_ADMINISTRATOR' AND p.permission_code LIKE 'PUR.REQ.%' ON CONFLICT DO NOTHING;
INSERT INTO role_permissions(role_id,permission_id) SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p WHERE r.role_code='ROLE_USER' AND p.permission_code IN ('PUR.REQ.VIEW','PUR.REQ.EDIT') ON CONFLICT DO NOTHING;
INSERT INTO role_permissions(role_id,permission_id) SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p WHERE r.role_code='ROLE_VIEWER' AND p.permission_code='PUR.REQ.VIEW' ON CONFLICT DO NOTHING;

-- A requisition can create at most one PO, even under concurrent conversion attempts.
CREATE UNIQUE INDEX IF NOT EXISTS ux_purchase_requisition_single_po_link ON document_links(company_id,source_doc_kind,source_doc_id,target_doc_kind) WHERE source_doc_kind='PRQ' AND target_doc_kind='PO';
-- @shoutech-migration 086_purchase_document_attachments.sql 2195DF394845E2A227FCFC63C1EBC4A61E8E91618FD9DC98BAB738D7EE87FCB3 1F8BF56A42745E630E1D50E5E9B5FDE93F1AF97DCF860DA06005AB55B9539EBD 1F8BF56A42745E630E1D50E5E9B5FDE93F1AF97DCF860DA06005AB55B9539EBD 1F8BF56A42745E630E1D50E5E9B5FDE93F1AF97DCF860DA06005AB55B9539EBD
-- 086 - Secure metadata for purchase-document attachments (PostgreSQL). Bytes remain outside the database.
CREATE TABLE IF NOT EXISTS purchase_document_attachments (
 attachment_id UUID PRIMARY KEY, company_id UUID NOT NULL, document_id BIGINT NOT NULL,
 storage_key TEXT NOT NULL UNIQUE, original_name TEXT NOT NULL, media_type TEXT NOT NULL,
 byte_length BIGINT NOT NULL CHECK(byte_length>0 AND byte_length<=26214400), sha256 CHAR(64) NOT NULL,
 uploaded_by TEXT NOT NULL, uploaded_at TIMESTAMPTZ NOT NULL DEFAULT now(),
 is_deleted BOOLEAN NOT NULL DEFAULT false, deleted_by TEXT, deleted_at TIMESTAMPTZ);
CREATE INDEX IF NOT EXISTS ix_purchase_document_attachments_doc ON purchase_document_attachments(company_id,document_id,is_deleted,uploaded_at);
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES('PUR.ATTACH.VIEW','Purchase Attachment - View','PURCHASING','ATTACHMENT','VIEW','Purchase Attachment - View','Purchase Attachment - View','View purchase document attachments',530,false) ON CONFLICT(permission_code) DO NOTHING;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES('PUR.ATTACH.UPLOAD','Purchase Attachment - Upload','PURCHASING','ATTACHMENT','UPLOAD','Purchase Attachment - Upload','Purchase Attachment - Upload','Upload purchase document attachments',531,false) ON CONFLICT(permission_code) DO NOTHING;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES('PUR.ATTACH.DELETE','Purchase Attachment - Delete','PURCHASING','ATTACHMENT','DELETE','Purchase Attachment - Delete','Purchase Attachment - Delete','Soft-delete purchase document attachments',532,true) ON CONFLICT(permission_code) DO NOTHING;

INSERT INTO role_permissions(role_id,permission_id) SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p WHERE r.role_code='ROLE_ADMINISTRATOR' AND p.permission_code IN ('PUR.ATTACH.VIEW','PUR.ATTACH.UPLOAD','PUR.ATTACH.DELETE') ON CONFLICT DO NOTHING;
INSERT INTO role_permissions(role_id,permission_id) SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p WHERE r.role_code='ROLE_USER' AND p.permission_code IN ('PUR.ATTACH.VIEW','PUR.ATTACH.UPLOAD') ON CONFLICT DO NOTHING;
INSERT INTO role_permissions(role_id,permission_id) SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p WHERE r.role_code='ROLE_VIEWER' AND p.permission_code='PUR.ATTACH.VIEW' ON CONFLICT DO NOTHING;
-- @shoutech-migration 087_purchase_landed_costs.sql F55EB113E74639DF0CB130FCDB40FDDB550C8EAC4BDA1252177D1B631B9FA6A2 9A7B466DF816F0F0B3970F9C4ADFDC8951891F1042977D9A6E20173004749E9D 9A7B466DF816F0F0B3970F9C4ADFDC8951891F1042977D9A6E20173004749E9D 9A7B466DF816F0F0B3970F9C4ADFDC8951891F1042977D9A6E20173004749E9D
-- 087 - Landed cost voucher and line allocation ledger (PostgreSQL).
-- PostgreSQL counterpart of sqlite/087_purchase_landed_costs.sql.
CREATE TABLE IF NOT EXISTS purchase_landed_costs (
    landed_cost_id UUID NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    doc_no TEXT NOT NULL,
    doc_date DATE NOT NULL,
    receipt_doc_id BIGINT NOT NULL,
    supplier_id BIGINT,
    supplier_name TEXT,
    currency_code TEXT NOT NULL,
    exchange_rate NUMERIC NOT NULL CHECK (exchange_rate > 0),
    total_amount NUMERIC NOT NULL DEFAULT 0,
    total_base_amount NUMERIC NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Draft' CHECK (status IN ('Draft','Posted','Cancelled')),
    journal_entry_id UUID,
    source_reversal_id UUID,
    created_by TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    posted_by TEXT,
    posted_at TIMESTAMPTZ,
    UNIQUE(company_id, doc_no),
    FOREIGN KEY(receipt_doc_id) REFERENCES purchase_documents(doc_id)
);
CREATE INDEX IF NOT EXISTS ix_purchase_landed_cost_receipt
    ON purchase_landed_costs(company_id, receipt_doc_id, status);

CREATE TABLE IF NOT EXISTS purchase_landed_cost_charges (
    charge_id UUID NOT NULL PRIMARY KEY,
    landed_cost_id UUID NOT NULL,
    charge_code TEXT NOT NULL,
    description TEXT NOT NULL,
    allocation_basis TEXT NOT NULL CHECK (allocation_basis IN ('Value','Quantity','Weight','Volume','Manual')),
    amount NUMERIC NOT NULL,
    base_amount NUMERIC NOT NULL,
    expense_account_code TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    FOREIGN KEY(landed_cost_id) REFERENCES purchase_landed_costs(landed_cost_id)
);
CREATE INDEX IF NOT EXISTS ix_purchase_landed_cost_charges_header
    ON purchase_landed_cost_charges(landed_cost_id);

CREATE TABLE IF NOT EXISTS purchase_landed_cost_allocations (
    allocation_id UUID NOT NULL PRIMARY KEY,
    landed_cost_id UUID NOT NULL,
    charge_id UUID NOT NULL,
    company_id TEXT NOT NULL,
    receipt_doc_id BIGINT NOT NULL,
    receipt_line_id BIGINT NOT NULL,
    material_id BIGINT NOT NULL,
    warehouse_id BIGINT,
    base_quantity NUMERIC NOT NULL CHECK (base_quantity > 0),
    allocated_base_amount NUMERIC NOT NULL,
    unit_cost_addition NUMERIC NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE(charge_id, receipt_line_id),
    FOREIGN KEY(landed_cost_id) REFERENCES purchase_landed_costs(landed_cost_id),
    FOREIGN KEY(charge_id) REFERENCES purchase_landed_cost_charges(charge_id)
);
CREATE INDEX IF NOT EXISTS ix_purchase_landed_cost_allocations_receipt
    ON purchase_landed_cost_allocations(company_id, receipt_doc_id, receipt_line_id);

CREATE TABLE IF NOT EXISTS purchase_landed_cost_events (
    event_id UUID NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    landed_cost_id UUID NOT NULL,
    action TEXT NOT NULL,
    actor TEXT NOT NULL,
    reason TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_purchase_landed_cost_events_header
    ON purchase_landed_cost_events(company_id, landed_cost_id, created_at);

INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES
('PUR.LANDEDCOST.VIEW','Purchase Landed Cost - View','PURCHASING','LANDEDCOST','VIEW','Purchase Landed Cost - View','Purchase Landed Cost - View','View landed-cost vouchers and allocations',540,false),
('PUR.LANDEDCOST.EDIT','Purchase Landed Cost - Edit','PURCHASING','LANDEDCOST','EDIT','Purchase Landed Cost - Edit','Purchase Landed Cost - Edit','Create and edit landed-cost drafts',541,false),
('PUR.LANDEDCOST.POST','Purchase Landed Cost - Post','PURCHASING','LANDEDCOST','POST','Purchase Landed Cost - Post','Purchase Landed Cost - Post','Post landed costs to inventory valuation and general ledger',542,true),
('PUR.LANDEDCOST.REVERSE','Purchase Landed Cost - Reverse','PURCHASING','LANDEDCOST','REVERSE','Purchase Landed Cost - Reverse','Purchase Landed Cost - Reverse','Reverse posted landed-cost vouchers with audit',543,true)
ON CONFLICT(permission_code) DO NOTHING;
INSERT INTO role_permissions(role_id,permission_id)
SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code='ROLE_ADMINISTRATOR' AND p.permission_code LIKE 'PUR.LANDEDCOST.%'
ON CONFLICT DO NOTHING;
INSERT INTO role_permissions(role_id,permission_id)
SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code='ROLE_USER' AND p.permission_code IN ('PUR.LANDEDCOST.VIEW','PUR.LANDEDCOST.EDIT')
ON CONFLICT DO NOTHING;
INSERT INTO role_permissions(role_id,permission_id)
SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code='ROLE_VIEWER' AND p.permission_code='PUR.LANDEDCOST.VIEW'
ON CONFLICT DO NOTHING;
-- @shoutech-migration 088_lcsa.sql 54EC0BD9687A5570307F61AFF400D7F2A3DC362C6FC8E74A812EB998B71A2C26 C13F99A293CF2038E14C9454D7E7B639FC561CC0621D9081C60FA274BEA0FF2E C13F99A293CF2038E14C9454D7E7B639FC561CC0621D9081C60FA274BEA0FF2E C13F99A293CF2038E14C9454D7E7B639FC561CC0621D9081C60FA274BEA0FF2E
-- 088 - Persist the inventory / consumed-cost split for landed-cost postings (PostgreSQL).
-- This ledger is written in the same transaction as the stock revaluation and GL entry.
CREATE TABLE IF NOT EXISTS purchase_landed_cost_stock_adjustments (
    adjustment_id UUID NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    landed_cost_id UUID NOT NULL,
    allocation_id UUID NOT NULL,
    source_movement_id BIGINT NOT NULL,
    warehouse_id BIGINT NOT NULL,
    material_id BIGINT NOT NULL,
    allocated_base_amount NUMERIC NOT NULL,
    original_quantity NUMERIC NOT NULL CHECK (original_quantity > 0),
    remaining_quantity NUMERIC NOT NULL CHECK (remaining_quantity >= 0 AND remaining_quantity <= original_quantity),
    inventory_adjustment NUMERIC NOT NULL,
    consumed_adjustment NUMERIC NOT NULL,
    unit_cost_before NUMERIC NOT NULL,
    unit_cost_after NUMERIC NOT NULL CHECK (unit_cost_after >= 0),
    actor TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE(company_id, allocation_id),
    FOREIGN KEY(landed_cost_id) REFERENCES purchase_landed_costs(landed_cost_id),
    FOREIGN KEY(allocation_id) REFERENCES purchase_landed_cost_allocations(allocation_id)
);
CREATE INDEX IF NOT EXISTS ix_lc_stock_adj_material
    ON purchase_landed_cost_stock_adjustments(company_id, material_id, warehouse_id, created_at);
-- @shoutech-migration 090_lcbf.sql 60D0A9E5FCDC40D745182A8694314A029486AC6B53FEA3434F07B988A7224DB9 A05A8D55B7945F520AF53152BC7AB6C6207F3ED632E8EDD3C2C6C9325013775D A05A8D55B7945F520AF53152BC7AB6C6207F3ED632E8EDD3C2C6C9325013775D A05A8D55B7945F520AF53152BC7AB6C6207F3ED632E8EDD3C2C6C9325013775D
-- 090 - Backfill exact source receipt lines for legacy stock movements (PostgreSQL).
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
UPDATE material_stock_movements sm
SET source_document_line_id = candidates.line_id
FROM candidates
WHERE sm.stock_movement_id = candidates.stock_movement_id
  AND candidates.match_count = 1;
-- @shoutech-migration 091_prqc.sql A22A731C32BBFD11924181CC1D7F27433711E9F710E5D25E874F6A3E9D97B809 5611DFC8CA0254691C9CFD74470675F6C4A473837809F33B02E608AAEC9D87D4 5611DFC8CA0254691C9CFD74470675F6C4A473837809F33B02E608AAEC9D87D4 5611DFC8CA0254691C9CFD74470675F6C4A473837809F33B02E608AAEC9D87D4
-- 091 - Persist requisition conversion reservations without expanding the status CHECK constraint (PostgreSQL).
-- The UI projects Approved + conversion_started_at as the transient Converting state.
ALTER TABLE purchase_requisition_workflow
    ADD COLUMN IF NOT EXISTS conversion_started_at TIMESTAMPTZ;
-- @shoutech-migration 107_prq_completion.sql B0EA6AEF1304FD1091B195223A19B927A254CE97A1AC91F18984F8742BCDFF11 CE56B88B6FA84C0ED6650B398E8B18ECECF0899C41E1C39F085F097DF233CE6C CE56B88B6FA84C0ED6650B398E8B18ECECF0899C41E1C39F085F097DF233CE6C CE56B88B6FA84C0ED6650B398E8B18ECECF0899C41E1C39F085F097DF233CE6C
-- 107 - Purchase requisition completion (PostgreSQL): R2 cancel/close
-- states, R3 approval rounds, R4 RFQ conversion target, R5 required-by
-- date + request type, R6 real department column (stops the Notes
-- smuggling). Companion to docs/PURCHASE_CYCLE_COMPLETION.md section 2.

-- R5/R6: header facts on the shared document store (both tables, because
-- OrderDocumentRepository reads/writes both through one code path; the
-- columns are only meaningful for PRQ documents).
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS required_by_date DATE;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS request_type TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS department TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS required_by_date DATE;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS request_type TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS department TEXT;

-- R6: un-smuggle department values the old window wrote into Notes as a
-- leading "Department: <value>" line (trivially parseable shape only).
UPDATE purchase_documents
SET department = TRIM(substring(notes FROM 13 FOR position(E'\n' IN notes) - 13)),
    notes = substring(notes FROM position(E'\n' IN notes) + 1)
WHERE doc_kind = 'PRQ'
  AND department IS NULL
  AND notes LIKE 'Department: %'
  AND position(E'\n' IN notes) > 0
  AND TRIM(substring(notes FROM 13 FOR position(E'\n' IN notes) - 13)) <> '';

-- R2/R3/R4: widen the workflow status vocabulary, track the current
-- approval round, and record the RFQ conversion target.
ALTER TABLE purchase_requisition_workflow ADD COLUMN IF NOT EXISTS round_no INTEGER NOT NULL DEFAULT 1;
ALTER TABLE purchase_requisition_workflow ADD COLUMN IF NOT EXISTS rfq_doc_id BIGINT;
ALTER TABLE purchase_requisition_workflow DROP CONSTRAINT IF EXISTS purchase_requisition_workflow_status_check;
ALTER TABLE purchase_requisition_workflow ADD CONSTRAINT purchase_requisition_workflow_status_check
    CHECK(status IN ('Submitted','Approved','Rejected','Converted','Cancelled','Closed'));

-- R3: approvals gain their round; uniqueness moves to (round, step).
ALTER TABLE purchase_requisition_approvals ADD COLUMN IF NOT EXISTS round_no INTEGER NOT NULL DEFAULT 1;
ALTER TABLE purchase_requisition_approvals DROP CONSTRAINT IF EXISTS purchase_requisition_approvals_company_id_requisition_doc_id_step_no_key;
ALTER TABLE purchase_requisition_approvals ADD CONSTRAINT purchase_requisition_approvals_round_step_key
    UNIQUE(company_id, requisition_doc_id, round_no, step_no);

-- R4: a requisition can create at most one RFQ (mirrors the 085 PO index).
CREATE UNIQUE INDEX IF NOT EXISTS ux_purchase_requisition_single_rfq_link ON document_links(company_id,source_doc_kind,source_doc_id,target_doc_kind) WHERE source_doc_kind='PRQ' AND target_doc_kind='RFQ';

-- R2 RBAC: cancel and close follow the PUR.REQ.* pattern (085).
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES('PUR.REQ.CANCEL','Purchase Requisition - Cancel','PURCHASING','REQUISITION','CANCEL','Purchase Requisition - Cancel','Purchase Requisition - Cancel','Cancel a draft or in-flight purchase requisition before conversion',524,true) ON CONFLICT(permission_code) DO NOTHING;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES('PUR.REQ.CLOSE','Purchase Requisition - Close','PURCHASING','REQUISITION','CLOSE','Purchase Requisition - Close','Purchase Requisition - Close','Close a fully converted purchase requisition',525,true) ON CONFLICT(permission_code) DO NOTHING;
INSERT INTO role_permissions(role_id,permission_id) SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p WHERE r.role_code='ROLE_ADMINISTRATOR' AND p.permission_code LIKE 'PUR.REQ.%' ON CONFLICT DO NOTHING;
INSERT INTO role_permissions(role_id,permission_id) SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p WHERE r.role_code='ROLE_USER' AND p.permission_code='PUR.REQ.CANCEL' ON CONFLICT DO NOTHING;
-- @shoutech-migration 108_purchase_landed_cost_completion.sql 3D82354FC46B32D78A5429AC4226D149F4434182F5D298982573435BF6C3F39B EBB332B24B06306BE51A929502A245BF984A5259756A0860CDAC4B6E78D4EAFB EBB332B24B06306BE51A929502A245BF984A5259756A0860CDAC4B6E78D4EAFB EBB332B24B06306BE51A929502A245BF984A5259756A0860CDAC4B6E78D4EAFB
-- 108 - Landed-cost completion (PostgreSQL): per-charge payee + credit
-- mode, stored inventory/consumed split per allocation, multi-receipt
-- vouchers, and the dedicated Purchase Price Variance account (415).
-- SQLite counterpart: sqlite/108_purchase_landed_cost_completion.sql.

-- L1/L2: per-charge payee (parties.ledger_id; NULL = receipt supplier)
-- and credit mode: 'Payable' (default; voucher creates the liability) or
-- 'Linked' (liability already exists on linked_document_id, a posted
-- supplier document; posting reclasses out of linked_account_code, the
-- account that document originally debited, and creates NO new
-- liability). 'AlreadyBooked' remains in the CHECK only so drafts
-- written by the interim 108 build stay postable.
ALTER TABLE purchase_landed_cost_charges ADD COLUMN IF NOT EXISTS payee_party_id BIGINT;
ALTER TABLE purchase_landed_cost_charges ADD COLUMN IF NOT EXISTS credit_mode TEXT NOT NULL DEFAULT 'Payable'
    CHECK (credit_mode IN ('Payable','AlreadyBooked','Linked'));
ALTER TABLE purchase_landed_cost_charges ADD COLUMN IF NOT EXISTS linked_document_id BIGINT
    REFERENCES purchase_documents(doc_id);
ALTER TABLE purchase_landed_cost_charges ADD COLUMN IF NOT EXISTS linked_account_code TEXT;
CREATE INDEX IF NOT EXISTS ix_purchase_landed_cost_charges_linked
    ON purchase_landed_cost_charges(linked_document_id);

-- L3: inventory/consumed split stored at post time, replayed by reversal.
ALTER TABLE purchase_landed_cost_allocations ADD COLUMN IF NOT EXISTS inventory_amount NUMERIC;
ALTER TABLE purchase_landed_cost_allocations ADD COLUMN IF NOT EXISTS consumed_amount NUMERIC;

-- L4: every receipt a voucher draws from (header receipt_doc_id remains
-- the primary/first receipt).
CREATE TABLE IF NOT EXISTS purchase_landed_cost_receipts (
    landed_cost_id UUID NOT NULL,
    receipt_doc_id BIGINT NOT NULL,
    PRIMARY KEY (landed_cost_id, receipt_doc_id),
    FOREIGN KEY(landed_cost_id) REFERENCES purchase_landed_costs(landed_cost_id),
    FOREIGN KEY(receipt_doc_id) REFERENCES purchase_documents(doc_id)
);
CREATE INDEX IF NOT EXISTS ix_purchase_landed_cost_receipts_receipt
    ON purchase_landed_cost_receipts(receipt_doc_id, landed_cost_id);

INSERT INTO purchase_landed_cost_receipts (landed_cost_id, receipt_doc_id)
SELECT landed_cost_id, receipt_doc_id FROM purchase_landed_costs
ON CONFLICT DO NOTHING;

-- L5: Purchase Price Variance account (415) for every company carrying
-- the purchase group (account 402), under the same parent as 402.
-- Deterministic account id follows the migration-059 md5 pattern.
INSERT INTO chart_of_accounts
    (account_id, company_id, account_code, account_name, name_ar, account_type,
     parent_account_id, is_postable, is_active, is_deleted)
SELECT md5(c.company_id::text || ':108:' || '415')::uuid,
       c.company_id,
       '415',
       'Purchase Price Variance',
       'Purchase Price Variance',
       'EXPENSE',
       (SELECT p.parent_account_id FROM chart_of_accounts p
         WHERE p.company_id = c.company_id AND p.account_code = '402'),
       TRUE, TRUE, FALSE
FROM companies c
WHERE EXISTS (SELECT 1 FROM chart_of_accounts a
              WHERE a.company_id = c.company_id AND a.account_code = '402')
ON CONFLICT (company_id, account_code) DO NOTHING;
-- @shoutech-migration 109_purchase_invoice_due_date.sql F1F5225B446DDF86F361B69A3EDB0F7AABFF4EDB6A94D7F439393A408BFA7750 3F83503BC4F0EDD7CB5BFA5B47A15E74A5D495A37B48CD3091047C671517FAAD 3F83503BC4F0EDD7CB5BFA5B47A15E74A5D495A37B48CD3091047C671517FAAD 3F83503BC4F0EDD7CB5BFA5B47A15E74A5D495A37B48CD3091047C671517FAAD
-- 109 - Purchase invoice due date (PostgreSQL).
-- Y3 (payable aging by due date): purchase_documents gains the due date the
-- Accounts Payable aging report buckets open invoices by. NULL on rows that
-- predate this migration; readers fall back to doc_date, and new invoices
-- are stamped at creation (doc date + supplier credit_days unless the user
-- picked an explicit due date in InvoiceWin).
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS due_date TEXT;

CREATE INDEX IF NOT EXISTS ix_purchase_documents_due_date
    ON purchase_documents (company_id, doc_kind, status, due_date);
-- @shoutech-migration 111_p2p_b1_core.sql 1EDAB262D6C1D03AB0A27B0078211600444013CD8CDB4E29C667F9AE36114113 380DF0087422013880701DB6F9B3B1429844F84AF8D847308FC62D21D9C7098D 380DF0087422013880701DB6F9B3B1429844F84AF8D847308FC62D21D9C7098D 380DF0087422013880701DB6F9B3B1429844F84AF8D847308FC62D21D9C7098D
-- 111 - Purchase-cycle completion B1 core (PostgreSQL): P3 PO header
-- facts, P4 stored line UOM factor + per-line required-by, P5 stored
-- per_received / per_billed, Q2 rejected qty at receipt, Q3 receipt
-- delivery-note number + bill identity (I1 bill_date), I5 PRT own
-- credit-note reference, P6 amended_from link, Q4 receipt
-- over-tolerance on the purchase match policy.
-- Companion to docs/PURCHASE_CYCLE_COMPLETION.md section 2 (P/Q/I).

ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS payment_terms TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS incoterm TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS payment_terms TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS incoterm TEXT;

ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS bill_date DATE;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS bill_date DATE;

ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS delivery_note_no TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS delivery_note_no TEXT;

ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS supplier_credit_note_ref TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS supplier_credit_note_ref TEXT;

ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS per_received NUMERIC NOT NULL DEFAULT 0;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS per_billed NUMERIC NOT NULL DEFAULT 0;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS per_received NUMERIC NOT NULL DEFAULT 0;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS per_billed NUMERIC NOT NULL DEFAULT 0;

ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS amended_from_doc_id BIGINT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS amended_from_doc_id BIGINT;

ALTER TABLE purchase_document_lines ADD COLUMN IF NOT EXISTS uom_factor NUMERIC;
ALTER TABLE purchase_document_lines ADD COLUMN IF NOT EXISTS line_required_by DATE;
ALTER TABLE sales_document_lines ADD COLUMN IF NOT EXISTS uom_factor NUMERIC;
ALTER TABLE sales_document_lines ADD COLUMN IF NOT EXISTS line_required_by DATE;

ALTER TABLE purchase_document_lines ADD COLUMN IF NOT EXISTS rejected_qty NUMERIC NOT NULL DEFAULT 0;
ALTER TABLE purchase_document_lines ADD COLUMN IF NOT EXISTS reject_reason TEXT;
ALTER TABLE sales_document_lines ADD COLUMN IF NOT EXISTS rejected_qty NUMERIC NOT NULL DEFAULT 0;
ALTER TABLE sales_document_lines ADD COLUMN IF NOT EXISTS reject_reason TEXT;

ALTER TABLE purchase_document_lines ADD COLUMN IF NOT EXISTS tax_code TEXT;
ALTER TABLE sales_document_lines ADD COLUMN IF NOT EXISTS tax_code TEXT;

ALTER TABLE purchase_match_policies ADD COLUMN IF NOT EXISTS receipt_over_tolerance_pct NUMERIC NOT NULL DEFAULT 0;
-- @shoutech-migration 112_rfq_invitations_backfill.sql 52ED22A64F4548B2DF380DB320F8498D76FC9B57D2A072B7D7AC8FF8208AC5A0 ACFA4B587E60C056EA82AD90C83903A5D794FE0A199E8A1E290BDA57AD464BD5 ACFA4B587E60C056EA82AD90C83903A5D794FE0A199E8A1E290BDA57AD464BD5 ACFA4B587E60C056EA82AD90C83903A5D794FE0A199E8A1E290BDA57AD464BD5
-- 112 - RFQ supplier invitations (P1) + P5 backfill (PostgreSQL).

CREATE TABLE IF NOT EXISTS rfq_invitations (
    invitation_id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    company_id TEXT NOT NULL,
    rfq_doc_id BIGINT NOT NULL REFERENCES purchase_documents(doc_id),
    party_id BIGINT NOT NULL,
    party_code TEXT,
    party_name TEXT,
    status TEXT NOT NULL DEFAULT 'Invited'
        CHECK (status IN ('Invited', 'Quoted', 'Declined')),
    invited_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    responded_at TIMESTAMPTZ,
    created_by TEXT,
    UNIQUE (company_id, rfq_doc_id, party_id)
);

CREATE INDEX IF NOT EXISTS ix_rfq_invitations_rfq
    ON rfq_invitations (company_id, rfq_doc_id, status);

-- P5 backfill (see the SQLite companion for the semantics).
UPDATE purchase_documents
SET per_received = CASE
        WHEN (SELECT COALESCE(SUM(qty), 0) FROM purchase_document_lines WHERE doc_id = purchase_documents.doc_id) <= 0 THEN 0
        ELSE LEAST(100, ROUND((100.0 * GREATEST(0,
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
            ) / (SELECT SUM(qty) FROM purchase_document_lines WHERE doc_id = purchase_documents.doc_id))::numeric, 2))
    END,
    per_billed = CASE
        WHEN (SELECT COALESCE(SUM(qty), 0) FROM purchase_document_lines WHERE doc_id = purchase_documents.doc_id) <= 0 THEN 0
        ELSE LEAST(100, ROUND((100.0 * GREATEST(0,
            (SELECT COALESCE(SUM(l.qty), 0)
               FROM document_links dl
               JOIN purchase_documents r ON r.doc_id = dl.target_doc_id
                    AND r.doc_kind = 'PUR' AND r.status = 'Posted' AND r.is_deleted = 0
               JOIN purchase_document_lines l ON l.doc_id = r.doc_id
              WHERE dl.company_id = purchase_documents.company_id
                AND dl.source_doc_kind = 'PO' AND dl.source_doc_id = purchase_documents.doc_id
                AND dl.target_doc_kind = 'PUR')
            ) / (SELECT SUM(qty) FROM purchase_document_lines WHERE doc_id = purchase_documents.doc_id))::numeric, 2))
    END
WHERE doc_kind = 'PO' AND is_deleted = 0;
-- @shoutech-migration 113_prt_match_unwind.sql 8F1A55E3D6695BF16B65DAB7DA413ADB535D88889BAE80EB667F645761D8BEF8 77C6F394A672B2E266C00AB4FD26A107D3919C2C3AC177D523A477F0617469DB 77C6F394A672B2E266C00AB4FD26A107D3919C2C3AC177D523A477F0617469DB 77C6F394A672B2E266C00AB4FD26A107D3919C2C3AC177D523A477F0617469DB
-- 113 - PRT match unwind audit (PostgreSQL, I4). See the SQLite
-- companion for the design note.

CREATE TABLE IF NOT EXISTS purchase_return_match_unwinds (
    unwind_id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    company_id TEXT NOT NULL,
    return_doc_id BIGINT NOT NULL REFERENCES purchase_documents(doc_id),
    invoice_doc_id BIGINT NOT NULL,
    match_id TEXT NOT NULL,
    receipt_doc_id BIGINT NOT NULL,
    receipt_line_id BIGINT NOT NULL,
    invoice_line_id BIGINT NOT NULL,
    unwound_qty NUMERIC NOT NULL CHECK (unwound_qty > 0),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by TEXT
);

CREATE INDEX IF NOT EXISTS ix_prt_unwinds_return
    ON purchase_return_match_unwinds (company_id, return_doc_id);

CREATE INDEX IF NOT EXISTS ix_prt_unwinds_invoice
    ON purchase_return_match_unwinds (company_id, invoice_doc_id);
