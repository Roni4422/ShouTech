-- 086 - Secure metadata for purchase-document attachments (SQLite). Bytes remain outside the database.
CREATE TABLE IF NOT EXISTS purchase_document_attachments (
 attachment_id TEXT PRIMARY KEY, company_id TEXT NOT NULL, document_id INTEGER NOT NULL,
 storage_key TEXT NOT NULL UNIQUE, original_name TEXT NOT NULL, media_type TEXT NOT NULL,
 byte_length INTEGER NOT NULL CHECK(byte_length>0 AND byte_length<=26214400), sha256 TEXT NOT NULL,
 uploaded_by TEXT NOT NULL, uploaded_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
 is_deleted INTEGER NOT NULL DEFAULT 0 CHECK(is_deleted IN (0,1)), deleted_by TEXT, deleted_at TEXT);
CREATE INDEX IF NOT EXISTS ix_purchase_document_attachments_doc ON purchase_document_attachments(company_id,document_id,is_deleted,uploaded_at);
CREATE TABLE IF NOT EXISTS purchase_attachment_events (
 event_id INTEGER PRIMARY KEY AUTOINCREMENT, company_id TEXT NOT NULL, attachment_id TEXT NOT NULL,
 document_id INTEGER NOT NULL, action TEXT NOT NULL, actor TEXT NOT NULL, sha256 TEXT NOT NULL,
 created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
CREATE INDEX IF NOT EXISTS ix_purchase_attachment_events_doc ON purchase_attachment_events(company_id,document_id,created_at);
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES('PUR.ATTACH.VIEW','Purchase Attachment - View','PURCHASING','ATTACHMENT','VIEW','Purchase Attachment - View','Purchase Attachment - View','View purchase document attachments',530,0);
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES('PUR.ATTACH.UPLOAD','Purchase Attachment - Upload','PURCHASING','ATTACHMENT','UPLOAD','Purchase Attachment - Upload','Purchase Attachment - Upload','Upload purchase document attachments',531,0);
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES('PUR.ATTACH.DELETE','Purchase Attachment - Delete','PURCHASING','ATTACHMENT','DELETE','Purchase Attachment - Delete','Purchase Attachment - Delete','Soft-delete purchase document attachments',532,1);
