-- 092 - Purchase requisition completion (SQLite): R2 cancel/close states,
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
