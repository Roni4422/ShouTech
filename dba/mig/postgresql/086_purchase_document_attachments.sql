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
