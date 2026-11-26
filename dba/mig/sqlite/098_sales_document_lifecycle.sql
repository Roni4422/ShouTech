-- ============================================================================
-- 098 - Sales document lifecycle: revisions, approval workflow, generalized
-- attachments (SQLite). Sales cycle W23 (Sales_Cycle.txt sections 2, 3, 5, 14).
--
--   * sales_documents.revision_no / approval_state: every chain + native
--     sales document carries its revision number (starts at 1, incremented
--     by every amendment) and its approval state (NotRequired / Pending /
--     Approved / Rejected). approval_state is meaningful only when the
--     company enables approval in sau_sales_settings (default OFF, so all
--     existing flows and documents are unaffected).
--   * sau_document_revisions (sau = Sales Audit, spec section 18): the full
--     prior version (header + lines as one JSON snapshot) preserved by an
--     amendment, keyed by the revision number it supersedes. Read-only.
--   * sau_approval_history: one row per workflow event (Submitted /
--     Approved / Rejected / Amended) with actor, reason and timestamps.
--   * sau_sales_settings: per-company policy; keyed by company CODE like
--     company_tax_settings (067). require_approval defaults OFF.
--   * purchase_document_attachments generalized (migration 086 extended,
--     no parallel table): owner_kind discriminates PURDOC (legacy purchase
--     documents, owner_ref = document id), SALDOC (sales documents,
--     owner_ref = document id) and PARTY (parties, owner_ref = party id).
--     Existing rows default to PURDOC with owner_ref = document_id.
--
-- RBAC: SALES.QUOTATION.APPROVE / SALES.ORDER.APPROVE / SALES.INVOICE.APPROVE
-- (sensitive, separated duty), SALES.QUOTATION.DIRECTINVOICE (sensitive,
-- authorized QUO -> SAL shortcut) and SALES.ATTACH.VIEW / UPLOAD / DELETE
-- seeded for ROLE_ADMINISTRATOR; plain users get attachment VIEW/UPLOAD
-- only (the 086 pattern). Display order continues after 097 (393).
--
-- Idempotent: @if-column-missing guards, CREATE TABLE/INDEX IF NOT EXISTS
-- and INSERT OR IGNORE + UPDATE seeds; the checksum ledger skips a re-run.
-- ============================================================================

-- @if-column-missing sales_documents.revision_no
ALTER TABLE sales_documents ADD COLUMN revision_no INTEGER NOT NULL DEFAULT 1;
-- @endif
-- @if-column-missing sales_documents.approval_state
ALTER TABLE sales_documents ADD COLUMN approval_state TEXT NOT NULL DEFAULT 'NotRequired';
-- @endif

CREATE TABLE IF NOT EXISTS sau_document_revisions (
    revision_id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    doc_id INTEGER NOT NULL,
    doc_kind TEXT NOT NULL,
    revision_no INTEGER NOT NULL,
    snapshot_json TEXT NOT NULL,
    change_reason TEXT,
    created_by TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (company_id, doc_id, revision_no)
);
CREATE INDEX IF NOT EXISTS ix_sau_revisions_doc
    ON sau_document_revisions(company_id, doc_id, revision_no);

CREATE TABLE IF NOT EXISTS sau_approval_history (
    history_id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    doc_id INTEGER NOT NULL,
    doc_kind TEXT NOT NULL,
    action TEXT NOT NULL CHECK (action IN ('Submitted', 'Approved', 'Rejected', 'Amended')),
    from_state TEXT NOT NULL,
    to_state TEXT NOT NULL,
    actor TEXT NOT NULL,
    reason TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_sau_approval_history_doc
    ON sau_approval_history(company_id, doc_id, created_at);

CREATE TABLE IF NOT EXISTS sau_sales_settings (
    company_code TEXT PRIMARY KEY,
    require_approval INTEGER NOT NULL DEFAULT 0 CHECK (require_approval IN (0, 1)),
    updated_by TEXT,
    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);

-- @if-column-missing purchase_document_attachments.owner_kind
ALTER TABLE purchase_document_attachments ADD COLUMN owner_kind TEXT NOT NULL DEFAULT 'PURDOC';
-- @endif
-- @if-column-missing purchase_document_attachments.owner_ref
ALTER TABLE purchase_document_attachments ADD COLUMN owner_ref TEXT;
-- @endif
UPDATE purchase_document_attachments
SET owner_ref = CAST(document_id AS TEXT)
WHERE owner_ref IS NULL;
CREATE INDEX IF NOT EXISTS ix_purchase_document_attachments_owner
    ON purchase_document_attachments(company_id, owner_kind, owner_ref, is_deleted, uploaded_at);

-- ----------------------------------------------------------------------------
-- RBAC seeds (097 pattern: INSERT OR IGNORE + UPDATE)
-- ----------------------------------------------------------------------------
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.QUOTATION.APPROVE','Sales Quotation - Approve','SALES','QUOTATION','APPROVE','عروض الأسعار - اعتماد','Sales Quotation - Approve','Approve or reject sales quotations submitted for approval (migration 098)',394,1);
UPDATE permissions SET group_code='SALES',feature_code='QUOTATION',operation_code='APPROVE',name_ar='عروض الأسعار - اعتماد',name_en='Sales Quotation - Approve',display_order=394,is_sensitive=1 WHERE permission_code='SALES.QUOTATION.APPROVE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.ORDER.APPROVE','Sales Order - Approve','SALES','ORDER','APPROVE','طلبات البيع - اعتماد','Sales Order - Approve','Approve or reject sales orders submitted for approval (migration 098)',395,1);
UPDATE permissions SET group_code='SALES',feature_code='ORDER',operation_code='APPROVE',name_ar='طلبات البيع - اعتماد',name_en='Sales Order - Approve',display_order=395,is_sensitive=1 WHERE permission_code='SALES.ORDER.APPROVE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.INVOICE.APPROVE','Sales Invoice - Approve','SALES','INVOICE','APPROVE','فواتير البيع - اعتماد','Sales Invoice - Approve','Approve or reject sales invoices submitted for approval (migration 098)',396,1);
UPDATE permissions SET group_code='SALES',feature_code='INVOICE',operation_code='APPROVE',name_ar='فواتير البيع - اعتماد',name_en='Sales Invoice - Approve',display_order=396,is_sensitive=1 WHERE permission_code='SALES.INVOICE.APPROVE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.QUOTATION.DIRECTINVOICE','Sales Quotation - Direct Invoice','SALES','QUOTATION','DIRECTINVOICE','عروض الأسعار - تحويل مباشر لفاتورة','Sales Quotation - Direct Invoice','Convert a posted quotation directly to a sales invoice, skipping the order stage (migration 098)',397,1);
UPDATE permissions SET group_code='SALES',feature_code='QUOTATION',operation_code='DIRECTINVOICE',name_ar='عروض الأسعار - تحويل مباشر لفاتورة',name_en='Sales Quotation - Direct Invoice',display_order=397,is_sensitive=1 WHERE permission_code='SALES.QUOTATION.DIRECTINVOICE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.ATTACH.VIEW','Sales Attachment - View','SALES','ATTACHMENT','VIEW','مرفقات المبيعات - عرض','Sales Attachment - View','View sales document and party attachments (migration 098)',398,0);
UPDATE permissions SET group_code='SALES',feature_code='ATTACHMENT',operation_code='VIEW',name_ar='مرفقات المبيعات - عرض',name_en='Sales Attachment - View',display_order=398,is_sensitive=0 WHERE permission_code='SALES.ATTACH.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.ATTACH.UPLOAD','Sales Attachment - Upload','SALES','ATTACHMENT','UPLOAD','مرفقات المبيعات - رفع','Sales Attachment - Upload','Upload sales document and party attachments (migration 098)',399,0);
UPDATE permissions SET group_code='SALES',feature_code='ATTACHMENT',operation_code='UPLOAD',name_ar='مرفقات المبيعات - رفع',name_en='Sales Attachment - Upload',display_order=399,is_sensitive=0 WHERE permission_code='SALES.ATTACH.UPLOAD';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.ATTACH.DELETE','Sales Attachment - Delete','SALES','ATTACHMENT','DELETE','مرفقات المبيعات - حذف','Sales Attachment - Delete','Soft-delete sales document and party attachments (migration 098)',400,1);
UPDATE permissions SET group_code='SALES',feature_code='ATTACHMENT',operation_code='DELETE',name_ar='مرفقات المبيعات - حذف',name_en='Sales Attachment - Delete',display_order=400,is_sensitive=1 WHERE permission_code='SALES.ATTACH.DELETE';

INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('SALES.QUOTATION.APPROVE','SALES.ORDER.APPROVE','SALES.INVOICE.APPROVE','SALES.QUOTATION.DIRECTINVOICE','SALES.ATTACH.VIEW','SALES.ATTACH.UPLOAD','SALES.ATTACH.DELETE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('SALES.ATTACH.VIEW','SALES.ATTACH.UPLOAD');
