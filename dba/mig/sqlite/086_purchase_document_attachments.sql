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
