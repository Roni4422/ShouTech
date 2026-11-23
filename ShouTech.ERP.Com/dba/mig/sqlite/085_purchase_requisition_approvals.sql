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
