-- ============================================================================
-- 098 - Sales document lifecycle: revisions, approval workflow, generalized
-- attachments (PostgreSQL). Sales cycle W23 (Sales_Cycle.txt sections 2, 3,
-- 5, 14). See the SQLite twin for the full rationale.
--
--   * sales_documents.revision_no / approval_state
--   * sau_document_revisions  - JSON snapshot of every superseded version
--   * sau_approval_history    - Submitted / Approved / Rejected / Amended
--   * sau_sales_settings      - per-company require_approval flag (default OFF)
--   * purchase_document_attachments generalized with owner_kind / owner_ref
--     (PURDOC legacy default, SALDOC sales documents, PARTY parties)
--
-- RBAC: SALES.QUOTATION.APPROVE / SALES.ORDER.APPROVE / SALES.INVOICE.APPROVE
-- / SALES.QUOTATION.DIRECTINVOICE (sensitive) and SALES.ATTACH.VIEW / UPLOAD
-- / DELETE seeded for ROLE_ADMINISTRATOR; plain users get attachment
-- VIEW/UPLOAD only (the 086 pattern). Display order continues after 097.
--
-- Idempotent: ADD COLUMN IF NOT EXISTS, CREATE TABLE/INDEX IF NOT EXISTS,
-- ON CONFLICT seeds; the checksum ledger skips a re-run.
-- ============================================================================

ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS revision_no INTEGER NOT NULL DEFAULT 1;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS approval_state TEXT NOT NULL DEFAULT 'NotRequired';

CREATE TABLE IF NOT EXISTS sau_document_revisions (
    revision_id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    doc_id BIGINT NOT NULL,
    doc_kind TEXT NOT NULL,
    revision_no INTEGER NOT NULL,
    snapshot_json TEXT NOT NULL,
    change_reason TEXT,
    created_by TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (company_id, doc_id, revision_no)
);
CREATE INDEX IF NOT EXISTS ix_sau_revisions_doc
    ON sau_document_revisions(company_id, doc_id, revision_no);

CREATE TABLE IF NOT EXISTS sau_approval_history (
    history_id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    doc_id BIGINT NOT NULL,
    doc_kind TEXT NOT NULL,
    action TEXT NOT NULL CHECK (action IN ('Submitted', 'Approved', 'Rejected', 'Amended')),
    from_state TEXT NOT NULL,
    to_state TEXT NOT NULL,
    actor TEXT NOT NULL,
    reason TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_sau_approval_history_doc
    ON sau_approval_history(company_id, doc_id, created_at);

CREATE TABLE IF NOT EXISTS sau_sales_settings (
    company_code TEXT PRIMARY KEY,
    require_approval BOOLEAN NOT NULL DEFAULT false,
    updated_by TEXT,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

ALTER TABLE purchase_document_attachments ADD COLUMN IF NOT EXISTS owner_kind TEXT NOT NULL DEFAULT 'PURDOC';
ALTER TABLE purchase_document_attachments ADD COLUMN IF NOT EXISTS owner_ref TEXT;
UPDATE purchase_document_attachments
SET owner_ref = CAST(document_id AS TEXT)
WHERE owner_ref IS NULL;
CREATE INDEX IF NOT EXISTS ix_purchase_document_attachments_owner
    ON purchase_document_attachments(company_id, owner_kind, owner_ref, is_deleted, uploaded_at);

-- ----------------------------------------------------------------------------
-- RBAC seeds (095 pattern: ON CONFLICT DO UPDATE + role grants)
-- ----------------------------------------------------------------------------
INSERT INTO permissions (permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.QUOTATION.APPROVE','Sales Quotation - Approve','SALES','QUOTATION','APPROVE','عروض الأسعار - اعتماد','Sales Quotation - Approve','Approve or reject sales quotations submitted for approval (migration 098)',394,true)
ON CONFLICT(permission_code) DO UPDATE SET permission_name=EXCLUDED.permission_name, group_code=EXCLUDED.group_code, feature_code=EXCLUDED.feature_code, operation_code=EXCLUDED.operation_code, name_ar=EXCLUDED.name_ar, name_en=EXCLUDED.name_en, description=EXCLUDED.description, display_order=EXCLUDED.display_order, is_sensitive=EXCLUDED.is_sensitive, is_active=true;
INSERT INTO permissions (permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.ORDER.APPROVE','Sales Order - Approve','SALES','ORDER','APPROVE','طلبات البيع - اعتماد','Sales Order - Approve','Approve or reject sales orders submitted for approval (migration 098)',395,true)
ON CONFLICT(permission_code) DO UPDATE SET permission_name=EXCLUDED.permission_name, group_code=EXCLUDED.group_code, feature_code=EXCLUDED.feature_code, operation_code=EXCLUDED.operation_code, name_ar=EXCLUDED.name_ar, name_en=EXCLUDED.name_en, description=EXCLUDED.description, display_order=EXCLUDED.display_order, is_sensitive=EXCLUDED.is_sensitive, is_active=true;
INSERT INTO permissions (permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.INVOICE.APPROVE','Sales Invoice - Approve','SALES','INVOICE','APPROVE','فواتير البيع - اعتماد','Sales Invoice - Approve','Approve or reject sales invoices submitted for approval (migration 098)',396,true)
ON CONFLICT(permission_code) DO UPDATE SET permission_name=EXCLUDED.permission_name, group_code=EXCLUDED.group_code, feature_code=EXCLUDED.feature_code, operation_code=EXCLUDED.operation_code, name_ar=EXCLUDED.name_ar, name_en=EXCLUDED.name_en, description=EXCLUDED.description, display_order=EXCLUDED.display_order, is_sensitive=EXCLUDED.is_sensitive, is_active=true;
INSERT INTO permissions (permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.QUOTATION.DIRECTINVOICE','Sales Quotation - Direct Invoice','SALES','QUOTATION','DIRECTINVOICE','عروض الأسعار - تحويل مباشر لفاتورة','Sales Quotation - Direct Invoice','Convert a posted quotation directly to a sales invoice, skipping the order stage (migration 098)',397,true)
ON CONFLICT(permission_code) DO UPDATE SET permission_name=EXCLUDED.permission_name, group_code=EXCLUDED.group_code, feature_code=EXCLUDED.feature_code, operation_code=EXCLUDED.operation_code, name_ar=EXCLUDED.name_ar, name_en=EXCLUDED.name_en, description=EXCLUDED.description, display_order=EXCLUDED.display_order, is_sensitive=EXCLUDED.is_sensitive, is_active=true;
INSERT INTO permissions (permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.ATTACH.VIEW','Sales Attachment - View','SALES','ATTACHMENT','VIEW','مرفقات المبيعات - عرض','Sales Attachment - View','View sales document and party attachments (migration 098)',398,false)
ON CONFLICT(permission_code) DO UPDATE SET permission_name=EXCLUDED.permission_name, group_code=EXCLUDED.group_code, feature_code=EXCLUDED.feature_code, operation_code=EXCLUDED.operation_code, name_ar=EXCLUDED.name_ar, name_en=EXCLUDED.name_en, description=EXCLUDED.description, display_order=EXCLUDED.display_order, is_sensitive=EXCLUDED.is_sensitive, is_active=true;
INSERT INTO permissions (permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.ATTACH.UPLOAD','Sales Attachment - Upload','SALES','ATTACHMENT','UPLOAD','مرفقات المبيعات - رفع','Sales Attachment - Upload','Upload sales document and party attachments (migration 098)',399,false)
ON CONFLICT(permission_code) DO UPDATE SET permission_name=EXCLUDED.permission_name, group_code=EXCLUDED.group_code, feature_code=EXCLUDED.feature_code, operation_code=EXCLUDED.operation_code, name_ar=EXCLUDED.name_ar, name_en=EXCLUDED.name_en, description=EXCLUDED.description, display_order=EXCLUDED.display_order, is_sensitive=EXCLUDED.is_sensitive, is_active=true;
INSERT INTO permissions (permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.ATTACH.DELETE','Sales Attachment - Delete','SALES','ATTACHMENT','DELETE','مرفقات المبيعات - حذف','Sales Attachment - Delete','Soft-delete sales document and party attachments (migration 098)',400,true)
ON CONFLICT(permission_code) DO UPDATE SET permission_name=EXCLUDED.permission_name, group_code=EXCLUDED.group_code, feature_code=EXCLUDED.feature_code, operation_code=EXCLUDED.operation_code, name_ar=EXCLUDED.name_ar, name_en=EXCLUDED.name_en, description=EXCLUDED.description, display_order=EXCLUDED.display_order, is_sensitive=EXCLUDED.is_sensitive, is_active=true;

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('SALES.QUOTATION.APPROVE','SALES.ORDER.APPROVE','SALES.INVOICE.APPROVE','SALES.QUOTATION.DIRECTINVOICE','SALES.ATTACH.VIEW','SALES.ATTACH.UPLOAD','SALES.ATTACH.DELETE')
ON CONFLICT DO NOTHING;
INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('SALES.ATTACH.VIEW','SALES.ATTACH.UPLOAD')
ON CONFLICT DO NOTHING;
