-- 092 - Purchase requisition completion (PostgreSQL): R2 cancel/close
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
