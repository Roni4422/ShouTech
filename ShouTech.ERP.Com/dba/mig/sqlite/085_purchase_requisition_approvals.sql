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
